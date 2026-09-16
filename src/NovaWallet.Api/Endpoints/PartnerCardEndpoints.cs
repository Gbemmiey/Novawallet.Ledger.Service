using NovaWallet.Api.Http;
using static NovaWallet.Api.Core.Configuration.NovaWalletConstants;

namespace NovaWallet.Api.Endpoints
{
    public static class PartnerCardEndpoints
    {
        public static RouteGroupBuilder MapPartnerCardEndpoints(this RouteGroupBuilder group)
        {
            // 1. Group-level authorization, rate limiting & filtering for Partner routes
            var partnerApi = group
                .RequireAuthorization(AuthorizationPolicyConstants.PartnerOnly)
                .RequireRateLimiting(RateLimitingConstants.PerPartnerPolicy)
                .WithMetadata(new PartnerEndpointAttribute());

            // 2. Unified Cards Sub-Group (/api/v1/partners/cards)
            var cardsApi = partnerApi.MapGroup("/cards");

            // Sub-Resource: Card Requests -> /api/v1/partners/cards/requests
            var cardRequests = cardsApi.MapGroup("/requests")
                .WithTags("Partner Card Requests");

            return group;
        }
    }
}