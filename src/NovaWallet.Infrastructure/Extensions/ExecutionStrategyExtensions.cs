using Microsoft.EntityFrameworkCore;

namespace NovaWallet.Infrastructure.Extensions
{
    public static class ExecutionStrategyExtensions
    {
        public static async Task ExecuteInTransactionAsync(
            this DbContext dbContext,
            Func<Task> operation,
            CancellationToken ct = default)
        {
            var strategy = dbContext.Database.CreateExecutionStrategy();

            await strategy.ExecuteAsync(async () =>
            {
                await using var transaction = await dbContext.Database.BeginTransactionAsync(ct);
                await operation();
                await transaction.CommitAsync(ct);
            });
        }
    }
}