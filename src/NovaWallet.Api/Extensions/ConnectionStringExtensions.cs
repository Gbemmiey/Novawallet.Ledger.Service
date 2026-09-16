namespace NovaWallet.Api.Extensions
{
    public static class ConnectionStringExtensions
    {
        public static string ResolveDatabaseConnectionString(
            this IConfiguration configuration)
        {
            return configuration["NOVAWALLET_LEDGER_CONNECTION_STRING"]
                ?? throw new InvalidOperationException(
                    "Environment variable 'NOVAWALLET_LEDGER_CONNECTION_STRING' not found.");
        }
    }
}