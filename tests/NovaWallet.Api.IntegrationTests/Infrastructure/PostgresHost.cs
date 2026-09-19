using Npgsql;
using Testcontainers.PostgreSql;

namespace NovaWallet.Api.IntegrationTests.Infrastructure;

/// <summary>
/// One throwaway Postgres container shared by the whole test run, started on first use. Each
/// fixture gets its own freshly created database inside it, so fixtures stay isolated (the
/// reconciliation worker, for instance, sweeps every wallet in its database) without paying
/// for a container per fixture. Testcontainers' resource reaper removes the container when the
/// test process exits, so it is deliberately never disposed here.
/// </summary>
public static class PostgresHost
{
    private const string Image = "postgres:16-alpine";

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static PostgreSqlContainer? _container;

    /// <summary>Creates an empty database and returns a connection string pointing at it.</summary>
    public static async Task<string> CreateDatabaseAsync()
    {
        await Gate.WaitAsync();

        try
        {
            if (_container is null)
            {
                var container = new PostgreSqlBuilder().WithImage(Image).Build();
                await container.StartAsync();
                _container = container;
            }
        }
        finally
        {
            Gate.Release();
        }

        var databaseName = "nova_" + Guid.NewGuid().ToString("N");
        var adminConnectionString = _container.GetConnectionString();

        await using (var connection = new NpgsqlConnection(adminConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", connection);
            await command.ExecuteNonQueryAsync();
        }

        return new NpgsqlConnectionStringBuilder(adminConnectionString) { Database = databaseName }.ConnectionString;
    }
}
