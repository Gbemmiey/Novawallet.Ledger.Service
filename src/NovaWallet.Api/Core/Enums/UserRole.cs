namespace NovaWallet.Api.Core.Enums
{
    /// <summary>
    /// Mock-auth role claim embedded directly in the JWT by <see cref="Infrastructure.Providers.MockUserAuthHelper"/>.
    /// There is no Users table or real role/identity store anywhere in this codebase - the
    /// caller-supplied <c>Role</c> on <see cref="Dto.Login.MockLoginRequest"/> is trusted as-is,
    /// matching this project's stated mock-auth scope (see README). <see cref="AuthorizationPolicyConstants"/>
    /// wires <see cref="Admin"/> to the <c>AdminOnly</c> policy via <c>RequireRole</c>.
    /// </summary>
    public enum UserRole
    {
        Customer = 1,
        Admin = 2
    }
}
