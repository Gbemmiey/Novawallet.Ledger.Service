using NovaWallet.Application.Dto.Login;

namespace NovaWallet.Application.Abstractions
{
    public interface IMockUserAuthHelper
    {
        string GenerateToken(MockLoginRequest loginRequest);
    }
}