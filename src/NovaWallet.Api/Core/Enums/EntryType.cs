namespace NovaWallet.Api.Core.Enums
{
    /// <summary>
    /// Discriminates a single AccountEntry line as the debit or credit side of a
    /// double-entry posting. Stored as a string (see AccountEntryConfiguration),
    /// matching OutboxStatus's convention.
    /// </summary>
    public enum EntryType
    {
        Debit = 1,
        Credit = 2
    }
}
