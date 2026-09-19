using System.Security.Cryptography;
using System.Text;

namespace NovaWallet.Api.Infrastructure.Cache
{
    /// <summary>
    /// Builds HybridCache keys for the deposit idempotency fast-path, keyed on a
    /// SHA-256 hash of the inbound NIP callback's SessionId. This cache is purely
    /// a fast-path short-circuit for at-least-once redelivery of the same callback -
    /// the database (ExternalCreditRequests.SessionId / TransactionReference unique
    /// indexes) remains the authoritative source of truth on every cache miss.
    /// </summary>
    public static class DepositIdempotencyCacheKeys
    {
        private const string Prefix = "deposit:session:";

        public static string ForSessionId(string sessionId)
        {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId)));
            return $"{Prefix}{hash}";
        }
    }
}
