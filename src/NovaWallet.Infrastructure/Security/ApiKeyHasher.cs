using System.Security.Cryptography;
using System.Text;

namespace NovaWallet.Infrastructure.Security
{
    public static class ApiKeyHasher
    {
        /// <summary>
        /// Computes a deterministic HMAC-SHA256 hash of an API key using a secret server pepper.
        /// </summary>
        public static string HashApiKey(string rawApiKey)
        {
            var inputBytes = Encoding.UTF8.GetBytes(rawApiKey);
            var hashBytes = SHA256.HashData(inputBytes);
            return Convert.ToHexString(hashBytes);
        }
    }
}