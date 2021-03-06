﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using System.Threading;
using Atomix.Blockchain;
using Atomix.Blockchain.Ethereum;
using Atomix.Common;
using Atomix.Common.Abstract;
using Atomix.Core;
using Atomix.Core.Entities;
using Atomix.Swaps.Abstract;
using Atomix.Swaps.Ethereum.Tasks;
using Atomix.Swaps.Tasks;
using Atomix.Wallet.Abstract;
using Nethereum.Contracts;
using Nethereum.RPC.Eth.DTOs;
using Serilog;
using Nethereum.Web3;
using Atomix.Wallet;

namespace Atomix.Swaps.Ethereum
{
    public class EthereumSwap : CurrencySwap
    {
        public EthereumSwap(
            Currency currency,
            IAccount account,
            ISwapClient swapClient,
            IBackgroundTaskPerformer taskPerformer)
            : base(
                currency,
                account,
                swapClient,
                taskPerformer)
        {
        }

        private Atomix.Ethereum Eth => (Atomix.Ethereum)Currency;

        public override async Task BroadcastPaymentAsync(ClientSwap swap)
        {
            var lockTimeInSeconds = swap.IsInitiator
                ? DefaultInitiatorLockTimeInSeconds
                : DefaultAcceptorLockTimeInSeconds;

            var txs = (await CreatePaymentTxsAsync(swap, lockTimeInSeconds)
                .ConfigureAwait(false))
                .ToList();

            if (txs.Count == 0)
            {
                Log.Error("Can't create payment transactions");
                return;
            }

            var signResult = await SignPaymentTxsAsync(txs)
                .ConfigureAwait(false);

            if (!signResult)
                return;

            swap.PaymentTx = txs.First();
            swap.SetPaymentSigned();
            RaiseSwapUpdated(swap, SwapStateFlags.IsPaymentSigned);

            foreach (var tx in txs)
                await BroadcastTxAsync(swap, tx)
                    .ConfigureAwait(false);

            swap.PaymentTx = txs.First();
            swap.SetPaymentBroadcast();
            RaiseSwapUpdated(swap, SwapStateFlags.IsPaymentBroadcast);

            // start redeem control
            TaskPerformer.EnqueueTask(new EthereumRedeemControlTask
            {
                Currency = Currency,
                RefundTimeUtc = swap.TimeStamp.ToUniversalTime().AddSeconds(lockTimeInSeconds),
                Swap = swap,
                CompleteHandler = RedeemControlCompletedEventHandler,
                CancelHandler = RedeemControlCanceledEventHandler
            });
        }

        public override Task PrepareToReceiveAsync(ClientSwap swap)
        {
            // initiator waits "accepted" event, acceptor waits "initiated" event
            var handler = swap.IsInitiator
                ? SwapAcceptedEventHandler
                : (OnTaskDelegate)SwapInitiatedEventHandler;

            var lockTimeInSeconds = swap.IsInitiator
                ? DefaultAcceptorLockTimeInSeconds
                : DefaultInitiatorLockTimeInSeconds;

            var refundTimeStampUtcInSec = new DateTimeOffset(swap.TimeStamp.ToUniversalTime().AddSeconds(lockTimeInSeconds)).ToUnixTimeSeconds();

            TaskPerformer.EnqueueTask(new EthereumSwapInitiatedControlTask
            {
                Currency = Currency,
                Swap = swap,
                Interval = TimeSpan.FromSeconds(30),
                RefundTimestamp = refundTimeStampUtcInSec,
                CompleteHandler = handler,
                CancelHandler = SwapCanceledEventHandler
            });

            return Task.CompletedTask;
        }

        public override Task RestoreSwapAsync(ClientSwap swap)
        {
            return swap.IsSoldCurrency(Currency)
                ? RestoreForSoldCurrencyAsync(swap)
                : RestoreForPurchasedCurrencyAsync(swap);
        }

        public override async Task RedeemAsync(ClientSwap swap)
        {
            Log.Debug("Create redeem for swap {@swapId}", swap.Id);

            var walletAddress = (await Account.GetUnspentAddressesAsync(
                    currency: Currency,
                    amount: 0, // todo: account storage fee
                    fee: Eth.RedeemGasLimit,
                    feePrice: Eth.GasPriceInGwei,
                    isFeePerTransaction: true,
                    addressUsagePolicy: AddressUsagePolicy.UseOnlyOneAddress)
                .ConfigureAwait(false))
                .FirstOrDefault();

            if (walletAddress == null)
            {
                Log.Error("Insufficient funds for redeem");
                return;
            }

            var nonce = await EthereumNonceManager.Instance
                .GetNonce(Eth, walletAddress.Address)
                .ConfigureAwait(false);

            var message = new RedeemFunctionMessage
            {
                FromAddress = walletAddress.Address,
                HashedSecret = swap.SecretHash,
                Secret = swap.Secret,
                Nonce = nonce,
                GasPrice = Atomix.Ethereum.GweiToWei(Eth.GasPriceInGwei),
            };

            message.Gas = await EstimateGasAsync(message, new BigInteger(Eth.RedeemGasLimit))
                .ConfigureAwait(false);

            var txInput = message.CreateTransactionInput(Eth.SwapContractAddress);

            var redeemTx = new EthereumTransaction(Eth, txInput)
            {
                Type = EthereumTransaction.OutputTransaction
            };

            var signResult = await SignTransactionAsync(redeemTx)
                .ConfigureAwait(false);

            if (!signResult)
            {
                Log.Error("Transaction signing error");
                return;
            }

            swap.RedeemTx = redeemTx;
            swap.SetRedeemSigned();
            RaiseSwapUpdated(swap, SwapStateFlags.IsRedeemSigned);

            await BroadcastTxAsync(swap, redeemTx)
                .ConfigureAwait(false);

            swap.RedeemTx = redeemTx;
            swap.SetRedeemBroadcast();
            RaiseSwapUpdated(swap, SwapStateFlags.IsRedeemBroadcast);

            TaskPerformer.EnqueueTask(new TransactionConfirmationCheckTask
            {
                Currency = Currency,
                Swap = swap,
                Interval = DefaultConfirmationCheckInterval,
                TxId = redeemTx.Id,
                CompleteHandler = RedeemConfirmedEventHandler
            });
        }

        public override Task WaitForRedeemAsync(ClientSwap swap)
        {
            Log.Debug("Wait redeem for swap {@swapId}", swap.Id);

            // start redeem control
            TaskPerformer.EnqueueTask(new EthereumRedeemControlTask
            {
                Currency = Currency,
                RefundTimeUtc = swap.TimeStamp.ToUniversalTime().AddSeconds(DefaultAcceptorLockTimeInSeconds),
                Swap = swap,
                CompleteHandler = RedeemPartyControlCompletedEventHandler,
                CancelHandler = RedeemPartyControlCanceledEventHandler
            });

            return Task.CompletedTask;
        }

        public override async Task PartyRedeemAsync(ClientSwap swap)
        {
            Log.Debug("Create redeem for counterParty for swap {@swapId}", swap.Id);

            var walletAddress = (await Account
                .GetUnspentAddressesAsync(
                    currency: Currency,
                    amount: 0,
                    fee: Eth.RedeemGasLimit,
                    feePrice: Eth.GasPriceInGwei,
                    isFeePerTransaction: false,
                    addressUsagePolicy: AddressUsagePolicy.UseOnlyOneAddress)
                .ConfigureAwait(false))
                .FirstOrDefault();

            if (walletAddress == null)
            {
                Log.Error("Insufficient balance for party redeem. Cannot find the address containing the required amount of funds.");
                return;
            }

            var nonce = await EthereumNonceManager.Instance
                .GetNonce(Eth, walletAddress.Address)
                .ConfigureAwait(false);

            var message = new RedeemFunctionMessage
            {
                FromAddress = walletAddress.Address,
                HashedSecret = swap.SecretHash,
                Secret = swap.Secret,
                Nonce = nonce,
                GasPrice = Atomix.Ethereum.GweiToWei(Eth.GasPriceInGwei),
            };

            message.Gas = await EstimateGasAsync(message, new BigInteger(Eth.RedeemGasLimit))
                .ConfigureAwait(false);

            var txInput = message.CreateTransactionInput(Eth.SwapContractAddress);

            var redeemTx = new EthereumTransaction(Eth, txInput)
            {
                Type = EthereumTransaction.OutputTransaction
            };

            var signResult = await SignTransactionAsync(redeemTx)
                .ConfigureAwait(false);

            if (!signResult)
            {
                Log.Error("Transaction signing error");
                return;
            }

            await BroadcastTxAsync(swap, redeemTx)
                .ConfigureAwait(false);
        }

        private async Task RefundAsync(
            ClientSwap swap,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            Log.Debug("Create refund for swap {@swap}", swap.Id);

            var walletAddress = (await Account.GetUnspentAddressesAsync(
                    currency: Currency,
                    amount: 0, // todo: account storage fee
                    fee: Eth.RefundGasLimit,
                    feePrice: Eth.GasPriceInGwei,
                    isFeePerTransaction: true,
                    addressUsagePolicy: AddressUsagePolicy.UseOnlyOneAddress)
                .ConfigureAwait(false))
                .FirstOrDefault();

            if (walletAddress == null)
            {
                Log.Error("Insufficient funds for refund");
                return;
            }

            var nonce = await EthereumNonceManager.Instance
                .GetNonce(Eth, walletAddress.Address)
                .ConfigureAwait(false);

            var message = new RefundFunctionMessage
            {
                FromAddress = walletAddress.Address,
                HashedSecret = swap.SecretHash,
                GasPrice = Atomix.Ethereum.GweiToWei(Eth.GasPriceInGwei),
                Nonce = nonce,
            };

            message.Gas = await EstimateGasAsync(message, new BigInteger(Eth.RefundGasLimit))
                .ConfigureAwait(false);

            var txInput = message.CreateTransactionInput(Eth.SwapContractAddress);

            var refundTx = new EthereumTransaction(Eth, txInput)
            {
                Type = EthereumTransaction.OutputTransaction
            };

            var signResult = await SignTransactionAsync(refundTx, cancellationToken)
                .ConfigureAwait(false);

            if (!signResult)
            {
                Log.Error("Transaction signing error");
                return;
            }

            swap.RefundTx = refundTx;
            swap.SetRefundSigned();
            RaiseSwapUpdated(swap, SwapStateFlags.IsRefundSigned);

            await BroadcastTxAsync(swap, refundTx, cancellationToken)
                .ConfigureAwait(false);

            swap.RefundTx = refundTx;
            swap.SetRefundBroadcast();
            RaiseSwapUpdated(swap, SwapStateFlags.IsRefundBroadcast);

            TaskPerformer.EnqueueTask(new TransactionConfirmationCheckTask
            {
                Currency = Currency,
                Swap = swap,
                Interval = DefaultConfirmationCheckInterval,
                TxId = refundTx.Id,
                CompleteHandler = RefundConfirmedEventHandler
            });
        }

        private Task RestoreForSoldCurrencyAsync(ClientSwap swap)
        {
            if (swap.StateFlags.HasFlag(SwapStateFlags.IsPaymentBroadcast))
            {
                if (!(swap.PaymentTx is EthereumTransaction))
                {
                    Log.Error("Can't restore swap {@id}. Payment tx is null.", swap.Id);

                    return Task.CompletedTask;
                }

                var lockTimeInSeconds = swap.IsInitiator
                    ? DefaultInitiatorLockTimeInSeconds
                    : DefaultAcceptorLockTimeInSeconds;

                // start redeem control
                TaskPerformer.EnqueueTask(new EthereumRedeemControlTask
                {
                    Currency = Currency,
                    RefundTimeUtc = swap.TimeStamp.ToUniversalTime().AddSeconds(lockTimeInSeconds),
                    Swap = swap,
                    CompleteHandler = RedeemControlCompletedEventHandler,
                    CancelHandler = RedeemControlCanceledEventHandler
                });
            }
            else
            {
                if (DateTime.UtcNow < swap.TimeStamp.ToUniversalTime() + DefaultMaxSwapTimeout)
                {
                    if (swap.IsInitiator)
                    {
                        // todo: initiate swap

                        //await InitiateSwapAsync(swapState)
                        //    .ConfigureAwait(false);
                    }
                    else
                    {
                        // todo: request secret hash from server
                    }
                }
                else
                {
                    swap.Cancel();
                    RaiseSwapUpdated(swap, SwapStateFlags.IsCanceled);
                }
            }

            return Task.CompletedTask;
        }

        private async Task RestoreForPurchasedCurrencyAsync(ClientSwap swap)
        {
            if (swap.RewardForRedeem > 0 &&
                swap.StateFlags.HasFlag(SwapStateFlags.IsPaymentBroadcast))
            {
                // may be swap already redeemed by someone else
                await WaitForRedeemAsync(swap)
                    .ConfigureAwait(false);
            }
            else if (swap.StateFlags.HasFlag(SwapStateFlags.IsRedeemBroadcast) &&
                    !swap.StateFlags.HasFlag(SwapStateFlags.IsRedeemConfirmed))
            {
                if (!(swap.RedeemTx is EthereumTransaction redeemTx))
                {
                    Log.Error("Can't restore swap {@id}. Redeem tx is null", swap.Id);
                    return;
                }

                TaskPerformer.EnqueueTask(new TransactionConfirmationCheckTask
                {
                    Currency = Currency,
                    Swap = swap,
                    Interval = DefaultConfirmationCheckInterval,
                    TxId = redeemTx.Id,
                    CompleteHandler = RedeemConfirmedEventHandler
                });
            }
        }

        #region Event Handlers

        private void SwapInitiatedEventHandler(BackgroundTask task)
        {
            var initiatedControlTask = task as EthereumSwapInitiatedControlTask;
            var swap = initiatedControlTask?.Swap;

            if (swap == null)
                return;

            Log.Debug(
                "Initiator payment transaction received. Now counter party can broadcast payment tx for swap {@swapId}", 
                swap.Id);

            swap.SetHasPartyPayment();
            swap.SetPartyPaymentConfirmed();
            RaiseSwapUpdated(swap, SwapStateFlags.HasPartyPayment | SwapStateFlags.IsPartyPaymentConfirmed);

            InitiatorPaymentConfirmed?.Invoke(this, new SwapEventArgs(swap));
        }

        private async void SwapAcceptedEventHandler(BackgroundTask task)
        {
            var initiatedControlTask = task as EthereumSwapInitiatedControlTask;
            var swap = initiatedControlTask?.Swap;

            if (swap == null)
                return;

            try
            {
                Log.Debug(
                    "Acceptor's payment transaction received. Now initiator can do self redeem and do party redeem for acceptor (if needs and wants) for swap {@swapId}.",
                    swap.Id);

                swap.SetHasPartyPayment();
                swap.SetPartyPaymentConfirmed();
                RaiseSwapUpdated(swap, SwapStateFlags.HasPartyPayment | SwapStateFlags.IsPartyPaymentConfirmed);

                RaiseAcceptorPaymentConfirmed(swap);

                await RedeemAsync(swap)
                    .ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Log.Error(e, "Swap accepted error");
            }
        }

        private void SwapCanceledEventHandler(BackgroundTask task)
        {
            var initiatedControlTask = task as EthereumSwapInitiatedControlTask;
            var swap = initiatedControlTask?.Swap;

            if (swap == null)
                return;

            // todo: do smth here
            Log.Debug("Swap canceled due to wrong counter party params {@swapId}", swap.Id);
        }

        private void RedeemConfirmedEventHandler(BackgroundTask task)
        {
            var confirmationCheckTask = task as TransactionConfirmationCheckTask;
            var swap = confirmationCheckTask?.Swap;

            swap?.SetRedeemConfirmed();
            RaiseSwapUpdated(swap, SwapStateFlags.IsRedeemConfirmed);


        }

        private void RedeemControlCompletedEventHandler(BackgroundTask task)
        {
            var redeemControlTask = task as EthereumRedeemControlTask;
            var swap = redeemControlTask?.Swap;

            if (swap == null)
                return;

            Log.Debug("Handle redeem control completed event for swap {@swapId}", swap.Id);

            if (swap.IsAcceptor)
            {
                swap.Secret = redeemControlTask.Secret;
                RaiseSwapUpdated(swap, SwapStateFlags.HasSecret);

                RaiseAcceptorPaymentSpent(swap);
            }
        }

        private void RedeemControlCanceledEventHandler(BackgroundTask task)
        {
            var redeemControlTask = task as EthereumRedeemControlTask;
            var swap = redeemControlTask?.Swap;

            if (swap == null)
                return;

            Log.Debug("Handle redeem control canceled event for swap {@swapId}", swap.Id);

            TaskPerformer.EnqueueTask(new RefundTimeControlTask
            {
                Currency = Currency,
                RefundTimeUtc = redeemControlTask.RefundTimeUtc,
                Swap = swap,
                CompleteHandler = RefundTimeReachedEventHandler
            });
        }

        private void RefundTimeReachedEventHandler(BackgroundTask task)
        {
            var refundTimeControlTask = task as RefundTimeControlTask;
            var swap = refundTimeControlTask?.Swap;

            if (swap == null)
                return;

            Log.Debug("Refund time reached for swap {@swapId}", swap.Id);

            TaskPerformer.EnqueueTask(new EthereumRefundControlTask
            {
                Currency = Currency,
                Swap = swap,
                CompleteHandler = RefundConfirmedEventHandler,
                CancelHandler = RefundEventHandler
            });
        }

        private async void RefundEventHandler(BackgroundTask task)
        {
            var refundControlTask = task as EthereumRefundControlTask;
            var swap = refundControlTask?.Swap;

            if (swap == null)
                return;

            try
            {
                await RefundAsync(swap)
                    .ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Log.Error(e, "Refund error");
            }
        }

        private void RefundConfirmedEventHandler(BackgroundTask task)
        {
            var confirmationCheckTask = task as TransactionConfirmationCheckTask;
            var swap = confirmationCheckTask?.Swap;

            swap?.SetRefundConfirmed();
            RaiseSwapUpdated(swap, SwapStateFlags.IsRefundConfirmed);
        }

        private void RedeemPartyControlCompletedEventHandler(BackgroundTask task)
        {
            var redeemControlTask = task as EthereumRedeemControlTask;
            var swap = redeemControlTask?.Swap;

            if (swap == null)
                return;

            Log.Debug("Handle redeem party control completed event for swap {@swapId}", swap.Id);

            if (swap.IsAcceptor)
            {
                swap.Secret = redeemControlTask.Secret;
                swap.SetRedeemConfirmed();
                RaiseSwapUpdated(swap, SwapStateFlags.IsRedeemConfirmed);

                // get transactions & update balance for address
                TaskPerformer.EnqueueTask(new AddressBalanceUpdateTask
                {
                    Account = Account,
                    Address = swap.ToAddress,
                    Currency = Currency,
                });
            }
        }

        private void RedeemPartyControlCanceledEventHandler(BackgroundTask task)
        {
            var redeemControlTask = task as EthereumRedeemControlTask;
            var swap = redeemControlTask?.Swap;

            if (swap == null)
                return;

            Log.Debug("Handle redeem party control canceled event for swap {@swapId}", swap.Id);

            if (swap.Secret?.Length > 0) //note: using RefundTimeUtc = DefaultCounterPartyLockTimeHours, if secret is not revealed yet, counterparty is refunding in _soldcurrency side
            {
                //todo: Make some panic here
                Log.Error("Counter counterParty redeem need to be made for swap {@swapId}, using secret {@Secret}",
                    swap.Id,
                    Convert.ToBase64String(swap.Secret));
            }
        }

        #endregion Event Handlers

        #region Helpers

        private async Task<IEnumerable<EthereumTransaction>> CreatePaymentTxsAsync(
            ClientSwap swap,
            int lockTimeInSeconds,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            Log.Debug("Create payment transactions for swap {@swapId}", swap.Id);

            var requiredAmountInEth = AmountHelper.QtyToAmount(swap.Side, swap.Qty, swap.Price);
            var refundTimeStampUtcInSec = new DateTimeOffset(swap.TimeStamp.ToUniversalTime().AddSeconds(lockTimeInSeconds)).ToUnixTimeSeconds();
            var isInitTx = true;
            var rewardForRedeemInEth = swap.PartyRewardForRedeem;

            var unspentAddresses = (await Account
                .GetUnspentAddressesAsync(Eth, cancellationToken)
                .ConfigureAwait(false))
                .ToList()
                .SortList((a, b) => a.AvailableBalance().CompareTo(b.AvailableBalance()));

            var transactions = new List<EthereumTransaction>();

            foreach (var walletAddress in unspentAddresses)
            {
                Log.Debug("Create swap payment tx from address {@address} for swap {@swapId}", walletAddress.Address, swap.Id);

                var balanceInEth = (await Account
                    .GetAddressBalanceAsync(
                        currency: Eth,
                        address: walletAddress.Address,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false))
                    .Available;

                Log.Debug("Available balance: {@balance}", balanceInEth);

                var feeAmountInEth = isInitTx
                    ? (rewardForRedeemInEth == 0
                        ? Eth.InitiateFeeAmount
                        : Eth.InitiateWithRewardFeeAmount)
                    : Eth.AddFeeAmount;

                var amountInEth = Math.Min(balanceInEth - feeAmountInEth, requiredAmountInEth);

                if (amountInEth <= 0)
                {
                    Log.Warning(
                        "Insufficient funds at {@address}. Balance: {@balance}, feeAmount: {@feeAmount}, result: {@result}.",
                        walletAddress.Address,
                        balanceInEth,
                        feeAmountInEth,
                        amountInEth);

                    continue;
                }

                requiredAmountInEth -= amountInEth;

                var nonce = await EthereumNonceManager.Instance
                    .GetNonce(Eth, walletAddress.Address)
                    .ConfigureAwait(false);

                TransactionInput txInput;

                if (isInitTx)
                {
                    var message = new InitiateFunctionMessage
                    {
                        HashedSecret = swap.SecretHash,
                        Participant = swap.PartyAddress,
                        RefundTimestamp = refundTimeStampUtcInSec,
                        AmountToSend = Atomix.Ethereum.EthToWei(amountInEth),
                        FromAddress = walletAddress.Address,
                        GasPrice = Atomix.Ethereum.GweiToWei(Eth.GasPriceInGwei),
                        Nonce = nonce,
                        RedeemFee = Atomix.Ethereum.EthToWei(rewardForRedeemInEth)
                    };

                    var initiateGasLimit = rewardForRedeemInEth == 0
                        ? Eth.InitiateGasLimit
                        : Eth.InitiateWithRewardGasLimit;

                    message.Gas = await EstimateGasAsync(message, new BigInteger(initiateGasLimit))
                        .ConfigureAwait(false);

                    txInput = message.CreateTransactionInput(Eth.SwapContractAddress);
                }
                else
                {
                    var message = new AddFunctionMessage
                    {
                        HashedSecret = swap.SecretHash,
                        AmountToSend = Atomix.Ethereum.EthToWei(amountInEth),
                        FromAddress = walletAddress.Address,
                        GasPrice = Atomix.Ethereum.GweiToWei(Eth.GasPriceInGwei),
                        Nonce = nonce,
                    };

                    message.Gas = await EstimateGasAsync(message, new BigInteger(Eth.AddGasLimit))
                        .ConfigureAwait(false);

                    txInput = message.CreateTransactionInput(Eth.SwapContractAddress);
                }

                transactions.Add(new EthereumTransaction(Eth, txInput)
                {
                    Type = EthereumTransaction.OutputTransaction
                });

                if (isInitTx)
                    isInitTx = false;

                if (requiredAmountInEth == 0)
                    break;
            }

            if (requiredAmountInEth > 0)
            {
                Log.Warning("Insufficient funds (left {@requredAmount}).", requiredAmountInEth);
                return Enumerable.Empty<EthereumTransaction>();
            }

            return transactions;
        }

        private async Task<bool> SignPaymentTxsAsync(
            IEnumerable<EthereumTransaction> transactions,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            foreach (var transaction in transactions)
            {
                var signResult = await SignTransactionAsync(transaction, cancellationToken)
                    .ConfigureAwait(false);

                if (!signResult)
                {
                    Log.Error("Transaction signing error");
                    return false;
                }
            }

            return true;
        }

        private async Task<bool> SignTransactionAsync(
            EthereumTransaction tx,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var walletAddress = await Account
                .ResolveAddressAsync(
                    currency: tx.Currency,
                    address: tx.From,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return await Account.Wallet
                .SignAsync(
                    tx: tx,
                    address: walletAddress,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task BroadcastTxAsync(
            ClientSwap swap,
            EthereumTransaction tx,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var txId = await Eth.BlockchainApi
                .BroadcastAsync(tx, cancellationToken)
                .ConfigureAwait(false);

            if (txId == null)
                throw new Exception("Transaction Id is null");

            Log.Debug("TxId {@id} for swap {@swapId}", txId, swap.Id);

            // account new unconfirmed transaction
            await Account
                .UpsertTransactionAsync(
                    tx: tx,
                    updateBalance: true,
                    notifyIfUnconfirmed: true,
                    notifyIfBalanceUpdated: true,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            // todo: transaction receipt status control
        }

        private async Task<BigInteger> EstimateGasAsync<TMessage>(
            TMessage message,
            BigInteger defaultGas) where TMessage : FunctionMessage, new()
        {
            try
            {
                var web3 = new Web3(Web3BlockchainApi.UriByChain(Eth.Chain));
                var txHandler = web3.Eth.GetContractTransactionHandler<TMessage>();

                var estimatedGas = await txHandler
                    .EstimateGasAsync(Eth.SwapContractAddress, message)
                    .ConfigureAwait(false);

                Log.Debug("Estimated gas {@gas}", estimatedGas?.Value.ToString());

                return estimatedGas?.Value ?? defaultGas;
            }
            catch (Exception)
            {
                Log.Debug("Error while estimating fee");
            }

            return defaultGas;
        }

        #endregion Helpers
    }
}