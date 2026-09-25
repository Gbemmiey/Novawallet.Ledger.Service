using Microsoft.OpenApi.Models;
using NovaWallet.Application.Abstractions;
using NovaWallet.Application.Configuration;
using NovaWallet.Application.Dto.Login;
using NovaWallet.Application.Response;
using NovaWallet.Infrastructure.Http;

namespace NovaWallet.Api.Features.Transfers
{
    public static class TransferEndpoints
    {
        public static RouteGroupBuilder MapTransferEndpoints(this RouteGroupBuilder group)
        {
            group
                .MapPost("/transfer", SubmitTransfer)
                .WithName("SubmitTransfer")
                .WithTags("Transfers")
                .RequireAuthorization()
                .RequireRateLimiting(NovaWalletConstants.RateLimitingConstants.TransferPolicy)
                .Produces<ServiceApiResponse<WalletTransferResponse>>(StatusCodes.Status200OK)
                .WithOpenApi(operation =>
                {
                    operation.Parameters.Add(new OpenApiParameter
                    {
                        Name = NovaWalletConstants.IdempotencyKey,
                        In = ParameterLocation.Header,
                        Required = true,
                        Description = "Client-supplied idempotency token for this transfer. " +
                            "Replaying the same key with an identical payload returns the original " +
                            "result unchanged; reusing the same key with a different payload is " +
                            "rejected with 409 Conflict. Maximum length 128 characters.",
                        Schema = new OpenApiSchema
                        {
                            Type = "string",
                            MaxLength = 128
                        }
                    });

                    return operation;
                })
                .WithValidation<WalletTransferRequest>();

            // Requery: the stored outcome (Completed or Failed, with reason) of a transfer, by the
            // Idempotency-Key it was submitted under. Read budget is UserPolicy, not TransferPolicy.
            group
                .MapGet("/transfer/{idempotencyKey}", RequeryTransfer)
                .WithName("RequeryTransfer")
                .WithTags("Transfers")
                .RequireAuthorization()
                .RequireRateLimiting(NovaWalletConstants.RateLimitingConstants.UserPolicy)
                .Produces<ServiceApiResponse<WalletTransferStatusResponse>>(StatusCodes.Status200OK);

            return group;
        }

        private static async Task<IResult> RequeryTransfer(
            string idempotencyKey,
            HttpContext httpContext,
            ITransferService transferService,
            CancellationToken cancellationToken)
        {
            var response = await transferService.RequeryTransfer(idempotencyKey, cancellationToken);
            return response.ToResult(httpContext);
        }

        private static async Task<IResult> SubmitTransfer(
            WalletTransferRequest request,
            HttpContext httpContext,
            ITransferService transferService)
        {
            var response = await transferService.Transfer(request, CancellationToken.None);
            return response.ToResult(httpContext);
        }
    }
}
