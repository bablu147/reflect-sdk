namespace Reflect
{
    /// <summary>Outcome of a server-side purchase receipt verification.</summary>
    public enum ReflectVerificationStatus
    {
        /// <summary>Could not be determined (debug mode, network failure, no receipt).</summary>
        Unknown = 0,
        /// <summary>Store (Apple/Google) confirmed the receipt is genuine.</summary>
        Verified = 1,
        /// <summary>Store reported the receipt is invalid / not found.</summary>
        NotVerified = 2,
        /// <summary>The verification request itself failed.</summary>
        Failed = 3,
    }

    /// <summary>
    /// Result returned by <see cref="ReflectSDK.VerifyPurchase"/> /
    /// <see cref="ReflectSDK.VerifyAndTrackPurchase"/>. Adjust parity:
    /// <c>AdjustPurchaseVerificationResult</c> (Status / Code / Message).
    /// </summary>
    public sealed class ReflectVerificationResult
    {
        public ReflectVerificationStatus Status;
        public int    Code;
        public string Message;

        public ReflectVerificationResult() { }
        public ReflectVerificationResult(ReflectVerificationStatus status, int code, string message)
        {
            Status = status; Code = code; Message = message;
        }
    }
}
