using NovaWallet.Api.Core.Configuration;
using NovaWallet.Api.Core.Dto.Login;
using NovaWallet.Api.Core.Models.Response;
using NovaWallet.Api.Core.Services;
using NovaWallet.Api.Infrastructure.Http;

namespace NovaWallet.Api.Features.Deposits
{
    /// <summary>
    /// Defines inbound NIP deposit (credit) endpoints.
    /// </summary>
    public static class DepositEndpoints
    {
        /// <summary>
        /// Maps deposit endpoints to the application route group.
        /// </summary>
        /// <param name="group">The route group to which the deposit endpoints are mapped.</param>
        /// <returns>The route group builder.</returns>
        public static RouteGroupBuilder MapDepositEndpoints(
            this RouteGroupBuilder group)
        {
            group
                .MapPost("/credit", SubmitDeposit)
                .WithName("SubmitDeposit")
                .WithTags("Deposits")
                .AllowAnonymous()
                .RequireRateLimiting(NovaWalletConstants.RateLimitingConstants.CreditPolicy)
                .Produces<ServiceApiResponse<NipSingleCreditResponse>>(StatusCodes.Status202Accepted)
                .WithValidation<NipSingleCreditRequest>();

            return group;
        }

        /// <summary>
        /// Accepts an inbound NIP single-credit callback. Validates the beneficiary account and
        /// durably records the deposit request, then returns immediately with HTTP 202 Accepted -
        /// the actual wallet crediting happens asynchronously via the deposit outbox/consumer, so
        /// this handler must not block on it (NIP callbacks time out quickly).
        ///
        /// Intentionally <see cref="AllowAnonymous"/>: this is an inbound callback from the NIP
        /// switch, not a customer-authenticated action - the switch does not carry (and cannot be
        /// expected to obtain) this API's own bearer tokens, mirroring real inbound-credit
        /// integration patterns. Caller identity/ownership is irrelevant here; trust is instead
        /// placed in the NIP payload itself (beneficiary account number, session ID, external
        /// transaction reference), which is exactly what <see cref="IDepositService"/> validates
        /// against.
        /// </summary>
        private static async Task<IResult> SubmitDeposit(
            NipSingleCreditRequest nipSingleCreditRequest,
            HttpContext httpContext,
            IDepositService depositService,
            CancellationToken cancellationToken)
        {
            var response = await depositService.SubmitDepositRequest(nipSingleCreditRequest, cancellationToken);
            return response.ToAcceptedResult(httpContext);
        }
    }
}
