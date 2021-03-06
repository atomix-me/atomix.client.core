﻿using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Atomix.Blockchain.Abstract;

namespace Atomix.Blockchain.Tezos
{
    public interface ITezosBlockchainApi : IBlockchainApi
    {
        Task<IEnumerable<IBlockchainTransaction>> GetTransactionsAsync(
            string address,
            CancellationToken cancellationToken = default(CancellationToken));

        Task<IEnumerable<IBlockchainTransaction>> GetTransactionsAsync(
            string address,
            int page,
            CancellationToken cancellationToken = default(CancellationToken));

        Task<bool> IsActiveAddress(
            string address,
            CancellationToken cancellationToken = default(CancellationToken));
    }
}