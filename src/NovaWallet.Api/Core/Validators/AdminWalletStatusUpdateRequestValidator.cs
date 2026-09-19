using FluentValidation;
using NovaWallet.Api.Core.Dto;

namespace NovaWallet.Api.Core.Validators
{
    public sealed class AdminWalletStatusUpdateRequestValidator : AbstractValidator<AdminWalletStatusUpdateRequest>
    {
        public AdminWalletStatusUpdateRequestValidator()
        {
            RuleFor(x => x.Status)
                .IsInEnum()
                .WithMessage("Status must be a valid WalletStatus value.");
        }
    }
}
