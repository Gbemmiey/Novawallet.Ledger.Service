using NovaWallet.Application.Dto.Login;
using NovaWallet.Application.Response;

namespace NovaWallet.Application.Abstractions
{
    public interface IAuthService
    {
        ServiceApiResponse<MockLoginResponse> Login(MockLoginRequest dto);
    }
}