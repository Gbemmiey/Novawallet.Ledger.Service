namespace NovaWallet.Api.Core.Dto
{
    /// <summary>
    /// Admin-facing projection of an <see cref="Models.AuditLog"/> row (see
    /// <c>AdminService.GetAuditLogs</c>). Unlike the wallet statement endpoint, this is not
    /// ownership-scoped - an AdminOnly-policy caller can see across wallets by design.
    /// </summary>
    public class AuditLogResponse
    {
        public Guid Id { get; set; }
        public Guid WalletId { get; set; }
        public string ActorSubject { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty;
        public long BalanceBeforeKobo { get; set; }
        public long BalanceAfterKobo { get; set; }
        public Guid CorrelationId { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}
