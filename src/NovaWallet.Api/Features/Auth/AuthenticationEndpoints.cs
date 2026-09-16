using NovaWallet.Api.Core.Dto.Login;
using NovaWallet.Api.Core.Services;
using NovaWallet.Api.Infrastructure.Http;
using static NovaWallet.Api.Core.Configuration.NovaWalletConstants;

namespace NovaWallet.Api.Features.Auth
{
    /// <summary>
    /// Defines authentication endpoints.
    /// </summary>
    public static class AuthenticationEndpoints
    {
        /// <summary>
        /// Maps authentication endpoints to the application route group.
        /// </summary>
        /// <param name="group">The route group to which the authentication endpoints are mapped.</param>
        /// <returns>The route group builder.</returns>
        public static RouteGroupBuilder MapAuthenticationEndpoints(
            this RouteGroupBuilder group)
        {
            group
                .MapPost("/login", Login)
                .WithTags("Authentication")
                .AllowAnonymous()
                .RequireRateLimiting(RateLimitingConstants.InternalAdminPolicy);

            return group;
        }

        private static IResult Login(
            MockLoginRequest request,
            HttpContext httpContext,
            IAuthService authService)
        {
            var response = authService.Login(request);
            return response.ToResult(httpContext);
        }
    }
}