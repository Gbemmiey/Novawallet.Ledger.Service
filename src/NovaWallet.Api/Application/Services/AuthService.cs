using NovaWallet.Api.Core.Dto.Login;
using NovaWallet.Api.Core.Models.Response;
using NovaWallet.Api.Core.Services;
using NovaWallet.Api.Infrastructure.Providers;

namespace NovaWallet.Api.Application.Services
{
    public sealed class AuthService : IAuthService
    {
        private readonly IMockUserAuthHelper _mockUserAuthHelper;
        private readonly ILogger<AuthService> _logger;

        public AuthService(IMockUserAuthHelper mockUserAuthHelper, ILogger<AuthService> logger)
        {
            _mockUserAuthHelper = mockUserAuthHelper;
            _logger = logger;
        }

        public ServiceApiResponse<MockLoginResponse> Login(MockLoginRequest dto)
        {
            if (dto is null)
            {
                return ServiceApiResponse<MockLoginResponse>.CreateFailure(
                    ResponseCodes.InvalidEntryDetected);
            }

            var accessToken = _mockUserAuthHelper.GenerateToken(dto);

            if (string.IsNullOrWhiteSpace(accessToken))
            {
                _logger.LogError("Mock authentication failed to generate an access token for UserId: {UserId}", dto.UserId);
                return ServiceApiResponse<MockLoginResponse>.SystemMalFunctioned();
            }

            return ServiceApiResponse<MockLoginResponse>.CreateSuccess(data: new MockLoginResponse
            {
                AccessToken = accessToken
            });
        }
    }
}