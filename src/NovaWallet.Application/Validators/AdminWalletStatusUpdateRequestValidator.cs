using FluentValidation;
using NovaWallet.Application.Dto;

namespace NovaWallet.Application.Validators
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
