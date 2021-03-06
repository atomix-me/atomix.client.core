﻿using System;
using System.Threading.Tasks;
using Atomix.Blockchain.Abstract;
using Atomix.Blockchain.Ethereum;
using Nethereum.JsonRpc.WebSocketClient;
using Nethereum.Web3;
using Serilog;

namespace Atomix.Swaps.Ethereum.Tasks
{
    public class EthereumRefundControlTask : BlockchainTask
    {
        private Atomix.Ethereum Eth => (Atomix.Ethereum) Currency;

        public override async Task<bool> CheckCompletion()
        {
            try
            {
                Log.Debug("Ethereum: check refund event");

                var wsUri = Web3BlockchainApi.WsUriByChain(Eth.Chain);
                var web3 = new Web3(new WebSocketClient(wsUri));

                var refundEventHandler = web3.Eth
                    .GetEvent<RefundedEventDTO>(Eth.SwapContractAddress);

                var filter = refundEventHandler
                    .CreateFilterInput<byte[]>(Swap.SecretHash);

                var events = await refundEventHandler
                    .GetAllChanges(filter)
                    .ConfigureAwait(false);

                if (events.Count > 0)
                {
                    Log.Debug("Refund event received");

                    CompleteHandler?.Invoke(this);
                    return true;
                }
            }
            catch (Exception e)
            {
                Log.Error(e, "Ethereum refund control task error");
            }

            CancelHandler?.Invoke(this);
            return true;
        }
    }
}