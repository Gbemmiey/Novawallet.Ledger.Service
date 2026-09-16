using NovaWallet.Api.Core.Dto.Login;
using NovaWallet.Api.Core.Models.Response;

namespace NovaWallet.Api.Core.Services
{
    public interface IAuthService
    {
        ServiceApiResponse<MockLoginResponse> Login(MockLoginRequest dto);
    }
}