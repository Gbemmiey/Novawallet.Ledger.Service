using NovaWallet.Api.Core.Dto.Login;

namespace NovaWallet.Api.Infrastructure.Providers
{
    public interface IMockUserAuthHelper
    {
        string GenerateToken(MockLoginRequest loginRequest);
    }
}