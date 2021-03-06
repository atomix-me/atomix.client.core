﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Atomix.Blockchain.Abstract;
using Atomix.Blockchain.Ethereum;
using Atomix.Common;
using Atomix.Core;
using Atomix.Core.Entities;
using Atomix.Wallet.Abstract;
using Serilog;

namespace Atomix.Wallet.Ethereum
{
    public class EthereumCurrencyAccount : CurrencyAccount
    {
        public EthereumCurrencyAccount(
            Currency currency,
            IHdWallet wallet,
            IAccountDataRepository dataRepository)
                : base(currency, wallet, dataRepository)
        {
        }

        #region Common

        private Atomix.Ethereum Eth => (Atomix.Ethereum) Currency;

        public override async Task<Error> SendAsync(
            IEnumerable<WalletAddress> from,
            string to,
            decimal amount,
            decimal fee,
            decimal feePrice,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var selectedAddresses = SelectUnspentAddresses(
                    from: from.ToList(),
                    amount: amount,
                    fee: fee,
                    feePrice: feePrice,
                    isFeePerTransaction: false,
                    addressUsagePolicy: AddressUsagePolicy.UseMinimalBalanceFirst)
                .ToList();

            if (!selectedAddresses.Any())
                return new Error(
                    code: Errors.InsufficientFunds,
                    description: "Insufficient funds");

            var feePerTx = Math.Round(fee / selectedAddresses.Count);

            if (feePerTx < Eth.GasLimit)
                return new Error(
                    code: Errors.InsufficientGas,
                    description: "Insufficient gas");

            var feeAmount = Eth.GetFeeAmount(feePerTx, feePrice);

            Log.Debug("Fee per transaction {@feePerTransaction}. Fee Amount {@feeAmount}",
                feePerTx,
                feeAmount);

            foreach (var (walletAddress, addressAmount) in selectedAddresses)
            {
                Log.Debug("Send {@amount} ETH from address {@address} with available balance {@balance}",
                    addressAmount,
                    walletAddress.Address,
                    walletAddress.AvailableBalance());

                var nonce = await EthereumNonceManager.Instance
                    .GetNonce(Eth, walletAddress.Address)
                    .ConfigureAwait(false);

                var tx = new EthereumTransaction(Eth)
                {
                    To = to.ToLowerInvariant(),
                    Amount = new BigInteger(Atomix.Ethereum.EthToWei(addressAmount)),
                    Nonce = nonce,
                    GasPrice = new BigInteger(Atomix.Ethereum.GweiToWei(feePrice)),
                    GasLimit = new BigInteger(feePerTx),
                    Type = EthereumTransaction.OutputTransaction
                };

                var signResult = await Wallet
                    .SignAsync(tx, walletAddress, cancellationToken)
                    .ConfigureAwait(false);

                if (!signResult)
                    return new Error(
                        code: Errors.TransactionSigningError,
                        description: "Transaction signing error");

                if (!tx.Verify())
                    return new Error(
                        code: Errors.TransactionVerificationError,
                        description: "Transaction verification error");

                var txId = await Currency.BlockchainApi
                    .BroadcastAsync(tx, cancellationToken)
                    .ConfigureAwait(false);

                if (txId == null)
                    return new Error(
                        code: Errors.TransactionBroadcastError,
                        description: "Transaction Id is null");

                Log.Debug("Transaction successfully sent with txId: {@id}", txId);

                await UpsertTransactionAsync(
                        tx: tx,
                        updateBalance: false,
                        notifyIfUnconfirmed: true,
                        notifyIfBalanceUpdated: false,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }

            await UpdateBalanceAsync(cancellationToken)
                .ConfigureAwait(false);

            return null;
        }

        public override async Task<Error> SendAsync(
            string to,
            decimal amount,
            decimal fee,
            decimal feePrice,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var unspentAddresses = (await DataRepository
                .GetUnspentAddressesAsync(Currency)
                .ConfigureAwait(false))
                .ToList();

            return await SendAsync(
                    from: unspentAddresses,
                    to: to,
                    amount: amount,
                    fee: fee,
                    feePrice: feePrice,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }

        public override async Task<decimal> EstimateFeeAsync(
            string to,
            decimal amount,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var unspentAddresses = (await DataRepository
                .GetUnspentAddressesAsync(Currency)
                .ConfigureAwait(false))
                .ToList();

            var selectedAddresses = SelectUnspentAddresses(
                    from: unspentAddresses,
                    amount: amount,
                    fee: Eth.GasLimit,
                    feePrice: Eth.GasPriceInGwei,
                    isFeePerTransaction: true,
                    addressUsagePolicy: AddressUsagePolicy.UseMinimalBalanceFirst)
                .ToList();

            var feeAmount = Eth.GetFeeAmount(Eth.GasLimit, Eth.GasPriceInGwei);

            if (!selectedAddresses.Any())
                return unspentAddresses.Count * feeAmount;

            return selectedAddresses.Count * feeAmount;
        }

        protected override async Task ResolveTransactionTypeAsync(
            IBlockchainTransaction tx,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (!(tx is EthereumTransaction ethTx))
                throw new ArgumentException("Invalid tx type", nameof(tx));

            var isFromSelf = await IsSelfAddressAsync(
                    address: ethTx.From,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var isToSelf = await IsSelfAddressAsync(
                    address: ethTx.To,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (isFromSelf && isToSelf)
                ethTx.Type = EthereumTransaction.SelfTransaction;
            else if (isFromSelf)
                ethTx.Type = EthereumTransaction.OutputTransaction;
            else if (isToSelf)
                ethTx.Type = EthereumTransaction.InputTransaction;
            else
                ethTx.Type = EthereumTransaction.UnknownTransaction;
        }

        #endregion Common

        #region Balances

        public override async Task UpdateBalanceAsync(
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var transactions = (await DataRepository
                .GetTransactionsAsync(Currency)
                .ConfigureAwait(false))
                .Cast<EthereumTransaction>()
                .ToList();

            // calculate balances
            var totalBalance = 0m;
            var totalUnconfirmedIncome = 0m;
            var totalUnconfirmedOutcome = 0m;
            var addressBalances = new Dictionary<string, WalletAddress>();

            foreach (var tx in transactions)
            {
                var addresses = new HashSet<string>();

                if (tx.Type == EthereumTransaction.OutputTransaction || tx.Type == EthereumTransaction.SelfTransaction)
                    addresses.Add(tx.From);
                if (tx.Type == EthereumTransaction.InputTransaction || tx.Type == EthereumTransaction.SelfTransaction)
                    addresses.Add(tx.To);

                foreach (var address in addresses)
                {
                    var isIncome = address == tx.To;
                    var isOutcome = address == tx.From;
                    var isConfirmed = tx.IsConfirmed();
                    var isFailed = !tx.ReceiptStatus;
                    var isInternal = tx.IsInternal;

                    // check generating tx failed
                    if (isInternal && !isFailed &&
                        await DataRepository
                            .GetTransactionByIdAsync(Currency, tx.Id)
                            .ConfigureAwait(false) is EthereumTransaction generatingTx)
                    {
                        isFailed = !generatingTx.ReceiptStatus;
                    }

                    var gas = tx.GasUsed != 0 ? tx.GasUsed : tx.GasLimit;

                    var income = isIncome && !isFailed ? Atomix.Ethereum.WeiToEth(tx.Amount) : 0;
                    var outcome = isOutcome
                        ? (!isFailed 
                            ? -Atomix.Ethereum.WeiToEth(tx.Amount + tx.GasPrice * gas)
                            : -Atomix.Ethereum.WeiToEth(tx.GasPrice * gas))
                        : 0;
    
                    if (addressBalances.TryGetValue(address, out var walletAddress))
                    {
                        walletAddress.Balance            += isConfirmed ? income + outcome : 0;
                        walletAddress.UnconfirmedIncome  += !isConfirmed ? income : 0;
                        walletAddress.UnconfirmedOutcome += !isConfirmed ? outcome : 0;
                    }
                    else
                    {
                        walletAddress = await DataRepository
                            .GetWalletAddressAsync(Currency, address)
                            .ConfigureAwait(false);

                        walletAddress.Balance            = isConfirmed ? income + outcome : 0;
                        walletAddress.UnconfirmedIncome  = !isConfirmed ? income : 0;
                        walletAddress.UnconfirmedOutcome = !isConfirmed ? outcome : 0;
                        walletAddress.HasActivity = true;

                        addressBalances.Add(address, walletAddress);
                    }

                    totalBalance            += isConfirmed ? income + outcome : 0;
                    totalUnconfirmedIncome  += !isConfirmed ? income : 0;
                    totalUnconfirmedOutcome += !isConfirmed ? outcome : 0;
                }
            }

            // upsert addresses
            await DataRepository
                .UpsertAddressesAsync(addressBalances.Values)
                .ConfigureAwait(false);

            Balance = totalBalance;
            UnconfirmedIncome = totalUnconfirmedIncome;
            UnconfirmedOutcome = totalUnconfirmedOutcome;

            RaiseBalanceUpdated(new CurrencyEventArgs(Currency));
        }

        public override async Task UpdateBalanceAsync(
            string address,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var transactions = (await DataRepository
                .GetTransactionsAsync(Currency)
                .ConfigureAwait(false))
                .Cast<EthereumTransaction>()
                .ToList();

            var walletAddress = await DataRepository
                .GetWalletAddressAsync(Currency, address)
                .ConfigureAwait(false);

            var balance = 0m;
            var unconfirmedIncome = 0m;
            var unconfirmedOutcome = 0m;

            foreach (var tx in transactions)
            {
                var isIncome = address == tx.To;
                var isOutcome = address == tx.From;
                var isConfirmed = tx.IsConfirmed();
                var isFailed = !tx.ReceiptStatus;
                var isInternal = tx.IsInternal;

                // check generating tx failed
                if (isInternal && !isFailed &&
                    await DataRepository
                        .GetTransactionByIdAsync(Currency, tx.Id)
                        .ConfigureAwait(false) is EthereumTransaction generatingTx)
                {
                    isFailed = !generatingTx.ReceiptStatus;
                }

                var gas = tx.GasUsed != 0 ? tx.GasUsed : tx.GasLimit;

                var income = isIncome && !isFailed ? Atomix.Ethereum.WeiToEth(tx.Amount) : 0;
                var outcome = isOutcome
                    ? (!isFailed
                        ? -Atomix.Ethereum.WeiToEth(tx.Amount + tx.GasPrice * gas)
                        : -Atomix.Ethereum.WeiToEth(tx.GasPrice * gas))
                    : 0;

                balance            += isConfirmed ? income + outcome : 0;
                unconfirmedIncome  += !isConfirmed ? income : 0;
                unconfirmedOutcome += !isConfirmed ? outcome : 0;
            }

            var balanceDifference            = balance - walletAddress.Balance;
            var unconfirmedIncomeDifference  = unconfirmedIncome - walletAddress.UnconfirmedIncome;
            var unconfirmedOutcomeDifference = unconfirmedOutcome - walletAddress.UnconfirmedOutcome;

            if (balanceDifference != 0 ||
                unconfirmedIncomeDifference != 0 ||
                unconfirmedOutcomeDifference != 0)
            {
                walletAddress.Balance            = balance;
                walletAddress.UnconfirmedIncome  = unconfirmedIncome;
                walletAddress.UnconfirmedOutcome = unconfirmedOutcome;
                walletAddress.HasActivity = true;

                await DataRepository.UpsertAddressAsync(walletAddress)
                    .ConfigureAwait(false);

                Balance += balanceDifference;
                UnconfirmedIncome += unconfirmedIncomeDifference;
                UnconfirmedOutcome += unconfirmedOutcomeDifference;

                RaiseBalanceUpdated(new CurrencyEventArgs(Currency));
            }
        }

        #endregion Balances

        #region Addresses

        public override async Task<IEnumerable<WalletAddress>> GetUnspentAddressesAsync(
            decimal amount,
            decimal fee,
            decimal feePrice,
            bool isFeePerTransaction,
            AddressUsagePolicy addressUsagePolicy,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var unspentAddresses = (await DataRepository
                .GetUnspentAddressesAsync(Currency)
                .ConfigureAwait(false))
                .ToList();

            var selectedAddresses = SelectUnspentAddresses(
                from: unspentAddresses,
                amount: amount,
                fee: fee,
                feePrice: feePrice,
                isFeePerTransaction: isFeePerTransaction,
                addressUsagePolicy: addressUsagePolicy);

            return ResolvePublicKeys(selectedAddresses
                .Select(w => w.Item1)
                .ToList());
        }

        private IEnumerable<(WalletAddress, decimal)> SelectUnspentAddresses(
            IList<WalletAddress> from,
            decimal amount,
            decimal fee,
            decimal feePrice,
            bool isFeePerTransaction,
            AddressUsagePolicy addressUsagePolicy)
        {
            if (addressUsagePolicy == AddressUsagePolicy.UseMinimalBalanceFirst)
            {
                from = from.ToList().SortList((a, b) => a.AvailableBalance().CompareTo(b.AvailableBalance()));
            }
            else if (addressUsagePolicy == AddressUsagePolicy.UseMaximumBalanceFirst)
            {
                from = from.ToList().SortList((a, b) => b.AvailableBalance().CompareTo(a.AvailableBalance()));
            }
            else if (addressUsagePolicy == AddressUsagePolicy.UseOnlyOneAddress)
            {
                var address = from.FirstOrDefault(w => w.AvailableBalance() >= amount + Currency.GetFeeAmount(fee, feePrice));

                return address != null
                    ? new List<(WalletAddress, decimal)> { (address, amount + Currency.GetFeeAmount(fee, feePrice)) }
                    : Enumerable.Empty<(WalletAddress, decimal)>();
            }

            for (var txCount = 1; txCount <= from.Count; ++txCount)
            {
                var result = new List<(WalletAddress, decimal)>();
                var requiredAmount = amount;

                var feePerTx = isFeePerTransaction
                    ? Currency.GetFeeAmount(fee, feePrice)
                    : Currency.GetFeeAmount(fee, feePrice) / txCount;

                var completed = false;

                foreach (var address in from)
                {
                    var availableBalance = address.AvailableBalance();

                    if (availableBalance <= feePerTx) // ignore address with balance less than fee
                        continue;

                    var amountToUse = Math.Min(Math.Max(availableBalance - feePerTx, 0), requiredAmount);

                    result.Add((address, amountToUse));
                    requiredAmount -= amountToUse;

                    if (requiredAmount <= 0)
                    {
                        completed = true;
                        break;
                    }

                    if (result.Count == txCount) // will need more transactions
                        break;
                }

                if (completed)
                    return result;
            }

            return Enumerable.Empty<(WalletAddress, decimal)>();
        }

        #endregion Addresses
    }
}