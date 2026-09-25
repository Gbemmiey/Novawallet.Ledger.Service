using Microsoft.EntityFrameworkCore;
using NovaWallet.Application.Abstractions;
using Npgsql;

namespace NovaWallet.Infrastructure.Data;

/// <summary>
/// Postgres-specific implementation of <see cref="IUniqueConstraintViolationDetector"/>. This is
/// the one place left in the codebase that pattern-matches <see cref="PostgresException"/> for
/// this purpose - it used to be duplicated as a private method in WalletService, TransferService
/// and DepositService (DepositConsumer, an Infrastructure worker, keeps its own copy since it
/// already references Npgsql directly).
/// </summary>
public sealed class NpgsqlUniqueConstraintViolationDetector : IUniqueConstraintViolationDetector
{
    public bool IsUniqueViolation(Exception exception) =>
        exception is DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } };

    public bool IsCheckViolation(Exception exception) =>
        exception is PostgresException { SqlState: PostgresErrorCodes.CheckViolation }
            || exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.CheckViolation };
}
