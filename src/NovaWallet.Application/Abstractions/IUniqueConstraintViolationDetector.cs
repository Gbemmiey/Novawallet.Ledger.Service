namespace NovaWallet.Application.Abstractions;

/// <summary>
/// Recognises whether a persistence-layer exception is a unique-constraint or a CHECK-constraint
/// violation, without Application-layer code referencing Npgsql directly (the previous shape:
/// four near-identical private <c>IsUniqueViolation</c> methods, one per service, each pattern-
/// matching <c>Npgsql.PostgresException</c> by hand). The concrete implementation
/// (<c>NovaWallet.Infrastructure.Data.NpgsqlUniqueConstraintViolationDetector</c>) is the only
/// place that still knows about Postgres error codes.
/// </summary>
public interface IUniqueConstraintViolationDetector
{
    /// <summary>True if the exception is (or wraps) a unique-constraint violation - e.g. a
    /// concurrent request winning an insert race on an Idempotency-Key or SessionId.</summary>
    bool IsUniqueViolation(Exception exception);

    /// <summary>True if the exception is (or wraps) a CHECK-constraint violation - e.g.
    /// <c>CHK_Wallet_AvailableBalanceKobo_NonNegative</c> firing as the last-resort backstop
    /// against a negative balance. Unlike <see cref="IsUniqueViolation"/> this isn't limited to
    /// <c>DbUpdateException</c>, since raw-SQL calls (<c>Database.SqlQuery</c>) surface Postgres
    /// errors directly rather than wrapped in a SaveChanges-specific exception type.</summary>
    bool IsCheckViolation(Exception exception);
}
