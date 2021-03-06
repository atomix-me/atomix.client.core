﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Atomix.Blockchain.Abstract;
using Atomix.Blockchain.Tezos.Internal;
using Atomix.Common;
using Atomix.Core.Entities;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;

namespace Atomix.Blockchain.Tezos
{
    public class TzScanApi : ITezosBlockchainApi
    {
        private const string Mainnet = "https://api3.tzscan.io/";
        private const string Alphanet = "https://api.alphanet.tzscan.io/";

        private const string MainnetRpc = "https://mainnet-node.tzscan.io";
        private const string AlphanetRpc = "http://alphanet-node.tzscan.io:80";
        //public const string ZeronetRpc = "https://zeronet-node.tzscan.io:80";

        internal class OperationHeader<T>
        {
            [JsonProperty("hash")]
            public string Hash { get; set; }
            [JsonProperty("block_hash")]
            public string BlockHash { get; set; }
            [JsonProperty("network_hash")]
            public string NetworkHash { get; set; }
            [JsonProperty("type")]
            public T Type { get; set; }
        }

        internal class Address
        {
            [JsonProperty("tz")]
            public string Tz { get; set; }
        }

        internal class Operation
        {
            [JsonProperty("kind")]
            public string Kind { get; set; }
            [JsonProperty("src")]
            public Address Source { get; set; }
            [JsonProperty("public_key")]
            public string PublicKey { get; set; }
            [JsonProperty("amount")]
            public string Amount { get; set; }
            [JsonProperty("destination")]
            public Address Destination { get; set; }
            [JsonProperty("str_parameters")]
            public string Parameters { get; set; }
            [JsonProperty("failed")]
            public bool Failed { get; set; }
            [JsonProperty("internal")]
            public bool Internal { get; set; }
            [JsonProperty("burn")]
            public long Burn { get; set; }
            [JsonProperty("counter")]
            public long Counter { get; set; }
            [JsonProperty("fee")]
            public long Fee { get; set; }
            [JsonProperty("gas_limit")]
            public string GasLimit { get; set; }
            [JsonProperty("storage_limit")]
            public string StorageLimit { get; set; }
            [JsonProperty("op_level")]
            public long OpLevel { get; set; }
            [JsonProperty("timestamp")]
            public DateTime TimeStamp { get; set; }
        }

        internal class OperationType<T>
        {
            [JsonProperty("kind")]
            public string Kind { get; set; }
            [JsonProperty("source")]
            public Address Source { get; set; }
            [JsonProperty("operations")]
            public List<T> Operations { get; set; }
        }

        private readonly Currency _currency;
        private readonly string _rpcProvider;
        private readonly string _apiBaseUrl;

        public TzScanApi(Currency currency, TezosNetwork network)
        {
            _currency = currency;

            switch (network)
            {
                case TezosNetwork.Mainnet:
                    _rpcProvider = MainnetRpc;
                    _apiBaseUrl = Mainnet;
                    break;
                case TezosNetwork.Alphanet:
                    _rpcProvider = AlphanetRpc;
                    _apiBaseUrl = Alphanet;
                    break;
                default:
                    throw new NotSupportedException("Network not supported");
            }
        }

        public async Task<decimal> GetBalanceAsync(
            string address,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var rpc = new Rpc(_rpcProvider);

            return await rpc.GetBalance(address)
                .ConfigureAwait(false);
        }

        public async Task<string> BroadcastAsync(
            IBlockchainTransaction transaction,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var tx = (TezosTransaction) transaction;

            var rpc = new Rpc(_rpcProvider);

            var opResults = await rpc
                .PreApplyOperations(tx.Head, tx.Operations, tx.SignedMessage.EncodedSignature)
                .ConfigureAwait(false);

            if (!opResults.Any())
                return null;

            string txId = null;

            foreach (var opResult in opResults)
                Log.Debug("OperationResult {@result}: {@opResult}", opResult.Succeeded, opResult.Data.ToString());

            if (opResults.Any() && opResults.All(op => op.Succeeded))
            {
                var injectedOperation = await rpc
                    .InjectOperations(tx.SignedMessage.SignedBytes)
                    .ConfigureAwait(false);

                txId = injectedOperation.ToString();
            }

            if (txId == null)
                Log.Error("TxId is null");

            tx.Id = txId;

            return tx.Id;
        }

        public async Task<IBlockchainTransaction> GetTransactionAsync(
            string txId,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var requestUri = $"v3/operation/{txId}";

            return await HttpHelper.GetAsync(
                    baseUri: _apiBaseUrl,
                    requestUri: requestUri,
                    responseHandler: responseContent =>
                    {
                        var operationHeader = JsonConvert.DeserializeObject<OperationHeader<OperationType<Operation>>>(responseContent);

                        return TxsFromOperation(operationHeader).FirstOrDefault();
                    },
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task<IEnumerable<IBlockchainTransaction>> GetTransactionsByIdAsync(
            string txId,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var requestUri = $"v3/operation/{txId}";

            return await HttpHelper.GetAsync(
                    baseUri: _apiBaseUrl,
                    requestUri: requestUri,
                    responseHandler: responseContent =>
                    {
                        var operationHeader = JsonConvert.DeserializeObject<OperationHeader<OperationType<Operation>>>(responseContent);

                        return TxsFromOperation(operationHeader);
                    },
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false) ?? Enumerable.Empty<IBlockchainTransaction>();
        }

        public async Task<IEnumerable<IBlockchainTransaction>> GetTransactionsAsync(
            string address,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var transactions = new List<IBlockchainTransaction>();

            for (var p = 0; ; p++)
            {
                var txsOnPage = (await GetTransactionsAsync(address, p, cancellationToken)
                    .ConfigureAwait(false))
                    .ToList();

                if (!txsOnPage.Any())
                    break;

                transactions.AddRange(txsOnPage);
            }

            return transactions;
        }

        public async Task<IEnumerable<IBlockchainTransaction>> GetTransactionsAsync(
            string address,
            int page,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var requestUri = $"v3/operations/{address}?type=Transaction&p={page}"; // TODO: use all types!!!

            return await HttpHelper.GetAsync<IEnumerable<IBlockchainTransaction>>(
                    baseUri: _apiBaseUrl,
                    requestUri: requestUri,
                    responseHandler: responseContent =>
                    {
                        var transactions = new List<IBlockchainTransaction>();

                        var operations =
                            JsonConvert.DeserializeObject<List<OperationHeader<OperationType<Operation>>>>(responseContent);

                        foreach (var operation in operations)
                            transactions.AddRange(TxsFromOperation(operation));

                        return transactions;
                    },
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false) ?? Enumerable.Empty<IBlockchainTransaction>();
        }

        public async Task<bool> IsActiveAddress(
            string address,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var requestUri = $"v3/number_operations/{address}?type=Transaction";

            return await HttpHelper.GetAsync(
                    baseUri: _apiBaseUrl,
                    requestUri: requestUri,
                    responseHandler: responeContent =>
                    {
                        var operationsCount = JsonConvert.DeserializeObject<JArray>(responeContent)
                            .FirstOrDefault()?.Value<long>() ?? 0;

                        return operationsCount > 0;
                    },
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }

        private IEnumerable<IBlockchainTransaction> TxsFromOperation(
            OperationHeader<OperationType<Operation>> operationHeader)
        {
            var txs = new List<IBlockchainTransaction>();

            var internalCounters = new Dictionary<string, int>();

            foreach (var operation in operationHeader.Type.Operations)
            {
                try
                {
                    if (operation.Kind != OperationType.Transaction)
                    {
                        Log.Debug("Skip {@kind} operation", operation.Kind);
                        continue;
                    }

                    var internalIndex = 0;

                    if (operation.Internal)
                    {
                        if (internalCounters.TryGetValue(operationHeader.Hash, out var index))
                        {
                            internalIndex = ++index;
                            internalCounters[operationHeader.Hash] = internalIndex;
                        }
                        else
                        {
                            internalCounters.Add(operationHeader.Hash, internalIndex);
                        }
                    }

                    txs.Add(new TezosTransaction(_currency)
                    {
                        Id = operationHeader.Hash,
                        From = operation.Source.Tz,
                        To = operation.Destination.Tz,
                        Amount = decimal.Parse(operation.Amount),
                        Fee = operation.Fee,
                        GasLimit = decimal.Parse(operation.GasLimit),
                        StorageLimit = decimal.Parse(operation.StorageLimit),
                        Burn = operation.Burn,
                        Params = operation.Parameters != null
                            ? JObject.Parse(operation.Parameters)
                            : null,
                        Type = TezosTransaction.UnknownTransaction,
                        IsInternal = operation.Internal,
                        InternalIndex = internalIndex,

                        BlockInfo = new BlockInfo
                        {
                            Fees = operation.Fee,
                            FirstSeen = operation.TimeStamp,
                            BlockTime = operation.TimeStamp,
                            BlockHeight = operation.OpLevel,
                            Confirmations = operation.Failed ? 0 : 1
                        }
                    });
                }
                catch (Exception e)
                {
                    Log.Error(e, "Operation parse error");
                }
            }

            return txs;
        }

        public static string RpcByNetwork(TezosNetwork network)
        {
            switch (network)
            {
                case TezosNetwork.Mainnet:
                    return MainnetRpc;
                case TezosNetwork.Alphanet:
                    return AlphanetRpc;
                default:
                    throw new NotSupportedException("Network not supported");
            }
        }
    }
}