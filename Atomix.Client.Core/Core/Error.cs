﻿namespace Atomix.Core
{
    public class Error
    {
        public int Code { get; set; }
        public string Description { get; set; }
        public string RequestId { get; set; }
        public string OrderId { get; set; }
        public long? SwapId { get; set; }

        public Error()
            : this(0, null)
        {      
        }

        public Error(int code, string description)
        {
            Code = code;
            Description = description;
        }

        public Error(int code, string description, string requestId)
            : this(code, description) => RequestId = requestId;

        public Error(int code, string description, Entities.Order order)
            : this(code, description) => OrderId = order.ClientOrderId;

        public Error(int code, string description, Entities.Swap swap)
            : this(code, description) => SwapId = swap.Id;

        public Error(int code, string description, Entities.ClientSwap swap)
            : this(code, description) => SwapId = swap.Id;

        public override string ToString()
        {
            return $"{{Code: {Code}, Description: {Description}, RequestId: {RequestId}}}";
        }
    }

    public static class Errors
    {
        public const int NoError = 0;
        public const int NotAuthorized = 1;
        public const int AuthError = 2;
        public const int InvalidRequest = 3;
        public const int InvalidMessage = 4;
        public const int PermissionError = 5;

        public const int IsCriminalWallet = 1000;
        public const int InvalidSymbol = 1001;
        public const int InvalidPrice = 1002;
        public const int InvalidQty = 1003;
        public const int InvalidWallets = 1004;
        public const int InvalidSigns = 1005;
        public const int InvalidClientId = 1006;
        public const int InvalidOrderId = 1007;
        public const int InvalidTimeStamp = 1008;
        public const int InvalidConnection = 1009;
        public const int InvalidRewardForRedeem = 1010;

        public const int TransactionCreationError = 2000;
        public const int TransactionSigningError = 2001;
        public const int TransactionVerificationError = 2002;
        public const int TransactionBroadcastError = 2003;
        public const int InsufficientFunds = 2004;
        public const int InsufficientGas = 2005;
        public const int InsufficientFee = 2006;

        public const int SwapError = 3000;
        public const int SwapNotFound = 3001;
        public const int WrongSwapMessageOrder = 3002;
        public const int InvalidSecretHash = 3003;
        public const int InvalidPaymentTxId = 3004;
        public const int InvalidSpentPoint = 3005;
        public const int InvalidSwapPaymentTx = 3006;
        public const int InvalidRefundLockTime = 3007;
        public const int InvalidSwapPaymentTxAmount = 3008;

        public const int NoLiquidity = 4000;
    }
}