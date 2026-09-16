using Microsoft.AspNetCore.Mvc;
using NovaWallet.Api.Core.Dto;
using NovaWallet.Api.Core.Dto.CardRequests;
using NovaWallet.Api.Core.Models.Response;
using NovaWallet.Api.Core.Services;
using NovaWallet.Api.Http;
using NovaWallet.Api.Infrastructure.Http;
using NovaWallet.Api.Middlewares;
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
                .AddEndpointFilter<RequirePartnerAuthFilter>()
                .WithMetadata(new PartnerEndpointAttribute());

            // 2. Unified Cards Sub-Group (/api/v1/partners/cards)
            var cardsApi = partnerApi.MapGroup("/cards");

            // Sub-Resource: Card Requests -> /api/v1/partners/cards/requests
            var cardRequests = cardsApi.MapGroup("/requests")
                .WithTags("Partner Card Requests");

            cardRequests.MapPost("/", PartnerCreateCardRequest)
                .WithName(nameof(PartnerCreateCardRequest))
                .WithSummary("Create a new card issuance request")
                .WithValidation<CreateCardRequestDto>()
                .Produces<IServiceApiResponse<CreateCardResponse>>(StatusCodes.Status201Created)
                .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
                .Produces<ProblemDetails>(StatusCodes.Status409Conflict)
                .Produces<ProblemDetails>(StatusCodes.Status500InternalServerError);

            cardRequests.MapGet("/{reference}", PartnerRetrieveCardRequestByReference)
                .WithName(nameof(PartnerRetrieveCardRequestByReference))
                .WithSummary("Get card request details and status by reference")
                .Produces<IServiceApiResponse<CreateCardResponse>>(StatusCodes.Status200OK)
                .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
                .Produces<ProblemDetails>(StatusCodes.Status500InternalServerError);

            cardRequests.MapGet("/", PartnerRetrieveCardRequests)
                .WithName(nameof(PartnerRetrieveCardRequests))
                .WithSummary("Query card requests collection with optional filters")
                .WithDescription("Allows authenticated partners to search and paginate through card issuance requests.")
                .Produces<IServiceApiResponse<PagedResponse<CreateCardResponse>>>(StatusCodes.Status200OK)
                .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
                .Produces<ProblemDetails>(StatusCodes.Status500InternalServerError);

            // Sub-Resource: Card Types Catalog -> /api/v1/partners/cards/types
            var cardTypes = cardsApi.MapGroup("/types")
                .WithTags("Partner Card Types Catalog");

            cardTypes.MapGet("/", RetrieveCardTypes)
                .WithName(nameof(RetrieveCardTypes))
                .WithSummary("Retrieve available card types catalog")
                .Produces<IServiceApiResponse<List<CardTypeDto>>>(StatusCodes.Status200OK)
                .Produces<ProblemDetails>(StatusCodes.Status500InternalServerError);

            return group;
        }

        private static async Task<IResult> PartnerCreateCardRequest(
            CreateCardRequestDto dto,
            ICardService cardService,
            HttpContext httpContext,
            LinkGenerator linkGenerator,
            CancellationToken ct)
        {
            var result = await cardService.PartnerCreateRequestAsync(dto, ct);

            if (!result.IsSuccessful())
            {
                return result.ToResult(httpContext);
            }

            return result.ToCreatedResult(
                httpContext,
                linkGenerator,
                routeName: nameof(PartnerRetrieveCardRequestByReference),
                routeValues: new { reference = result.Data!.TransactionReference });
        }

        private static async Task<IResult> PartnerRetrieveCardRequestByReference(
            string reference,
            ICardService cardService,
            HttpContext httpContext,
            CancellationToken ct)
        {
            var result = await cardService.GetStatusAsync(reference, ct);
            return result.ToResult(httpContext);
        }

        private static async Task<IResult> PartnerRetrieveCardRequests(
            RetrieveCardRequestsQuery query,
            ICardService cardService,
            HttpContext httpContext,
            CancellationToken ct)
        {
            var result = await cardService.PartnerRetrieveCardRequestsAsync(query, ct);
            return result.ToResult(httpContext);
        }

        private static async Task<IResult> RetrieveCardTypes(
            ICardService cardService,
            HttpContext httpContext,
            CancellationToken ct)
        {
            var result = await cardService.RetrieveCardTypesAsync(ct);
            return result.ToResult(httpContext);
        }
    }
}