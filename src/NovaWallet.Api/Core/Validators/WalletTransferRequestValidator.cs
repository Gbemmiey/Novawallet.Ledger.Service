using FluentValidation;
using NovaWallet.Api.Core.Dto.Login;

namespace NovaWallet.Api.Core.Validators
{
    public sealed class WalletTransferRequestValidator : AbstractValidator<WalletTransferRequest>
    {
        public WalletTransferRequestValidator()
        {
            RuleFor(x => x.SourceWalletId)
                .NotEmpty()
                .Must(id => Guid.TryParse(id, out _))
                .WithMessage("SourceWalletId must be a valid identifier.");

            RuleFor(x => x.DestinationWalletId)
                .NotEmpty()
                .Must(id => Guid.TryParse(id, out _))
                .WithMessage("DestinationWalletId must be a valid identifier.");

            // Cross-field check, only meaningful once both ids are individually well-formed -
            // TransferService still re-checks this itself as defense-in-depth (README §7).
            RuleFor(x => x)
                .Must(x => !Guid.TryParse(x.SourceWalletId, out var source)
                    || !Guid.TryParse(x.DestinationWalletId, out var destination)
                    || source != destination)
                .WithMessage("SourceWalletId and DestinationWalletId must differ.")
                .WithName("DestinationWalletId");

            RuleFor(x => x.AmountInKobo)
                .GreaterThan(0);

            RuleFor(x => x.Narration)
                .MaximumLength(200)
                .When(x => !string.IsNullOrEmpty(x.Narration));
        }
    }
}
