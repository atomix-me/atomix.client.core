﻿using Atomix.Core.Entities;

namespace Atomix.Blockchain.Abstract
{
    public interface ITxOutput
    {
        uint Index { get; }
        long Value { get; }
        bool IsValid { get; }
        string TxId { get; }
        bool IsSpent { get; }
        ITxPoint SpentTxPoint { get; set; }
        string DestinationAddress(Currency currency);

        bool IsSwapPayment { get; }
    }
}