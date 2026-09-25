using FluentValidation;
using NovaWallet.Application.Dto.Login;

namespace NovaWallet.Application.Validators
{
    public sealed class WalletTransferRequestValidator : AbstractValidator<WalletTransferRequest>
    {
        public WalletTransferRequestValidator()
        {
            // The source wallet is inferred from the caller's session. A client that still
            // sends it is rejected rather than silently ignored, so nobody believes they
            // are choosing the source. Any non-null value fails, including an empty string.
#pragma warning disable CS0618 // deliberately reading the deprecated property to reject it
            RuleFor(x => x.SourceWalletId)
                .Null()
                .WithMessage("SourceWalletId is not accepted; the source wallet is inferred from your session.");
#pragma warning restore CS0618

            RuleFor(x => x.DestinationWalletId)
                .NotEmpty()
                .Must(id => Guid.TryParse(id, out _))
                .WithMessage("DestinationWalletId must be a valid identifier.");

            // "Source must differ from destination" can no longer be checked here, since the
            // source is resolved from the session. TransferService enforces it.

            RuleFor(x => x.AmountInKobo)
                .GreaterThan(0);

            RuleFor(x => x.Narration)
                .MaximumLength(200)
                .When(x => !string.IsNullOrEmpty(x.Narration));
        }
    }
}
