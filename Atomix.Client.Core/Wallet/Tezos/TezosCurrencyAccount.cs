﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Atomix.Blockchain.Abstract;
using Atomix.Blockchain.Tezos;
using Atomix.Common;
using Atomix.Core;
using Atomix.Core.Entities;
using Atomix.Wallet.Abstract;
using Serilog;

namespace Atomix.Wallet.Tezos
{
    public class TezosCurrencyAccount : CurrencyAccount
    {
        private class SelectedWalletAddress
        {
            public WalletAddress WalletAddress { get; set; }
            public decimal UsedAmount { get; set; }
            public decimal UsedFee { get; set; }
        }

        public TezosCurrencyAccount(
            Currency currency,
            IHdWallet wallet,
            IAccountDataRepository dataRepository)
                : base(currency, wallet, dataRepository)
        {
        }

        #region Common

        private Atomix.Tezos Xtz => (Atomix.Tezos) Currency;

        public override async Task<Error> SendAsync(
            IEnumerable<WalletAddress> from,
            string to,
            decimal amount,
            decimal fee,
            decimal feePrice,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var selectedAddresses = (await SelectUnspentAddressesAsync(
                    from: from.ToList(),
                    to: to,
                    amount: amount,
                    fee: fee,
                    isFeePerTransaction: false,
                    addressUsagePolicy: AddressUsagePolicy.UseMinimalBalanceFirst,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false))
                .ToList();

            if (!selectedAddresses.Any())
                return new Error(
                    code: Errors.InsufficientFunds,
                    description: "Insufficient funds");

            var feePerTxInMtz = Math.Round(fee.ToMicroTez() / selectedAddresses.Count);

            foreach (var selectedAddress in selectedAddresses)
            {
                var addressAmountMtz = selectedAddress.UsedAmount.ToMicroTez();

                Log.Debug("Send {@amount} XTZ from address {@address} with available balance {@balance}",
                    addressAmountMtz,
                    selectedAddress.WalletAddress.Address,
                    selectedAddress.WalletAddress.AvailableBalance());

                var tx = new TezosTransaction(Xtz)
                {
                    From = selectedAddress.WalletAddress.Address,
                    To = to,
                    Amount = Math.Round(addressAmountMtz, 0),
                    Fee = feePerTxInMtz,
                    GasLimit = Xtz.GasLimit,
                    StorageLimit = Xtz.StorageLimit,
                    Type = TezosTransaction.OutputTransaction
                };

                var signResult = await Wallet
                    .SignAsync(tx, selectedAddress.WalletAddress, cancellationToken)
                    .ConfigureAwait(false);

                if (!signResult)
                    return new Error(
                        code: Errors.TransactionSigningError,
                        description: "Transaction signing error");

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

            var selectedAddresses = (await SelectUnspentAddressesAsync(
                    from: unspentAddresses,
                    to: to,
                    amount: amount,
                    fee: Xtz.Fee.ToTez(),
                    isFeePerTransaction: true,
                    addressUsagePolicy: AddressUsagePolicy.UseMinimalBalanceFirst,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false))
                .ToList();

            if (!selectedAddresses.Any())
                return unspentAddresses.Count * Xtz.Fee.ToTez();

            return selectedAddresses.Sum(s => s.UsedFee);
        }

        protected override async Task ResolveTransactionTypeAsync(
            IBlockchainTransaction tx,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (!(tx is TezosTransaction xtzTx))
                throw new ArgumentException("Invalid tx type", nameof(tx));

            var isFromSelf = await IsSelfAddressAsync(
                    address: xtzTx.From,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var isToSelf = await IsSelfAddressAsync(
                    address: xtzTx.To,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (isFromSelf && isToSelf)
                xtzTx.Type = TezosTransaction.SelfTransaction;
            else if (isFromSelf)
                xtzTx.Type = TezosTransaction.OutputTransaction;
            else if (isToSelf)
                xtzTx.Type = TezosTransaction.InputTransaction;
            else
                xtzTx.Type = TezosTransaction.UnknownTransaction;
        }

        #endregion Common

        #region Balances

        public override async Task UpdateBalanceAsync(
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var transactions = (await DataRepository
                .GetTransactionsAsync(Currency)
                .ConfigureAwait(false))
                .Cast<TezosTransaction>()
                .ToList();

            // calculate unconfirmed balances
            var totalUnconfirmedIncome = 0m;
            var totalUnconfirmedOutcome = 0m;

            var addresses = new Dictionary<string, WalletAddress>();

            foreach (var tx in transactions)
            {
                var selfAddresses = new HashSet<string>();

                if (tx.Type == TezosTransaction.OutputTransaction || tx.Type == TezosTransaction.SelfTransaction)
                    selfAddresses.Add(tx.From);
                if (tx.Type == TezosTransaction.InputTransaction || tx.Type == TezosTransaction.SelfTransaction)
                    selfAddresses.Add(tx.To);

                foreach (var address in selfAddresses)
                {
                    var isIncome = address == tx.To;
                    var isOutcome = address == tx.From;
                    var isConfirmed = tx.IsConfirmed();

                    var income = isIncome ? Atomix.Tezos.MtzToTz(tx.Amount) : 0;
                    var outcome = isOutcome ? -Atomix.Tezos.MtzToTz(tx.Amount + tx.Fee + tx.Burn) : 0;

                    if (addresses.TryGetValue(address, out var walletAddress))
                    {
                        walletAddress.UnconfirmedIncome += !isConfirmed ? income : 0;
                        walletAddress.UnconfirmedOutcome += !isConfirmed ? outcome : 0;
                    }
                    else
                    {
                        walletAddress = await DataRepository
                            .GetWalletAddressAsync(Currency, address)
                            .ConfigureAwait(false);

                        walletAddress.UnconfirmedIncome = !isConfirmed ? income : 0;
                        walletAddress.UnconfirmedOutcome = !isConfirmed ? outcome : 0;
                        walletAddress.HasActivity = true;

                        addresses.Add(address, walletAddress);
                    }

                    totalUnconfirmedIncome += !isConfirmed ? income : 0;
                    totalUnconfirmedOutcome += !isConfirmed ? outcome : 0;
                }
            }

            var totalBalance = 0m;
            var api = (ITezosBlockchainApi)Xtz.BlockchainApi;

            foreach (var wa in addresses.Values)
            {
                wa.Balance = (await api.GetBalanceAsync(wa.Address)
                    .ConfigureAwait(false))
                    .ToTez();

                totalBalance += wa.Balance;
            }

            // upsert addresses
            await DataRepository
                .UpsertAddressesAsync(addresses.Values)
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
            var walletAddress = await DataRepository
                .GetWalletAddressAsync(Currency, address)
                .ConfigureAwait(false);

            if (walletAddress == null)
                return;

            var api = (ITezosBlockchainApi) Xtz.BlockchainApi;

            var balance = (await api.GetBalanceAsync(address, cancellationToken)
                .ConfigureAwait(false))
                .ToTez();

            // calculate unconfirmed balances
            var unconfirmedTxs = (await DataRepository
                .GetUnconfirmedTransactionsAsync(Currency)
                .ConfigureAwait(false))
                .Cast<TezosTransaction>()
                .ToList();

            var unconfirmedIncome = 0m;
            var unconfirmedOutcome = 0m;

            foreach (var utx in unconfirmedTxs)
            {
                unconfirmedIncome += address == utx.To
                    ? Atomix.Tezos.MtzToTz(utx.Amount)
                    : 0;
                unconfirmedOutcome += address == utx.From
                    ? -Atomix.Tezos.MtzToTz(utx.Amount + utx.Fee + utx.Burn)
                    : 0;
            }

            var balanceDifference = balance - walletAddress.Balance;
            var unconfirmedIncomeDifference = unconfirmedIncome - walletAddress.UnconfirmedIncome;
            var unconfirmedOutcomeDifference = unconfirmedOutcome - walletAddress.UnconfirmedOutcome;

            if (balanceDifference != 0 ||
                unconfirmedIncomeDifference != 0 ||
                unconfirmedOutcomeDifference != 0)
            {
                walletAddress.Balance = balance;
                walletAddress.UnconfirmedIncome = unconfirmedIncome;
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

            var selectedAddresses = await SelectUnspentAddressesAsync(
                    from: unspentAddresses,
                    to: null,
                    amount: amount,
                    fee: fee,
                    isFeePerTransaction: isFeePerTransaction,
                    addressUsagePolicy: addressUsagePolicy,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return ResolvePublicKeys(selectedAddresses
                .Select(w => w.WalletAddress)
                .ToList());
        }

        private async Task<IEnumerable<SelectedWalletAddress>> SelectUnspentAddressesAsync(
            IList<WalletAddress> from,
            string to,
            decimal amount,
            decimal fee,
            bool isFeePerTransaction,
            AddressUsagePolicy addressUsagePolicy,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var activationFeeTez = 0m;

            if (to != null)
            {
                var api = (ITezosBlockchainApi) Xtz.BlockchainApi;

                var isActive = await api
                    .IsActiveAddress(to, cancellationToken)
                    .ConfigureAwait(false);

                activationFeeTez = !isActive
                    ? Xtz.ActivationFee.ToTez()
                    : 0;
            }

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
                var address = from.FirstOrDefault(w => w.AvailableBalance() >= amount + fee + activationFeeTez);

                return address != null
                    ? new List<SelectedWalletAddress> {
                        new SelectedWalletAddress
                        {
                            WalletAddress = address,
                            UsedAmount = amount,
                            UsedFee = fee + activationFeeTez
                        }
                    }
                    : Enumerable.Empty<SelectedWalletAddress>();
            }

            for (var txCount = 1; txCount <= from.Count; ++txCount)
            {
                var result = new List<SelectedWalletAddress>();
                var requiredAmount = amount;

                var feePerTx = isFeePerTransaction
                    ? fee
                    : fee / txCount;

                var firstTx = true;
                var completed = false;

                foreach (var address in from)
                {
                    var availableBalance = address.AvailableBalance();

                    var txFee = firstTx
                        ? feePerTx + activationFeeTez
                        : feePerTx;

                    if (availableBalance <= txFee) // ignore address with balance less than fee
                        continue;

                    var amountToUse = Math.Min(Math.Max(availableBalance - txFee, 0), requiredAmount);

                    result.Add(new SelectedWalletAddress
                    {
                        WalletAddress = address,
                        UsedAmount = amountToUse,
                        UsedFee = txFee
                    });
                    requiredAmount -= amountToUse;

                    if (requiredAmount <= 0)
                    {
                        completed = true;
                        break;
                    }

                    if (result.Count == txCount) // will need more transactions
                        break;

                    if (firstTx)
                        firstTx = false;
                }

                if (completed)
                    return result;
            }

            return Enumerable.Empty<SelectedWalletAddress>();
        }

        #endregion Addresses
    }
}