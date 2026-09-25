using FluentValidation;
using NovaWallet.Application.Dto.Login;

namespace NovaWallet.Application.Validators
{
    public sealed class MockLoginRequestValidator : AbstractValidator<MockLoginRequest>
    {
        public MockLoginRequestValidator()
        {
            RuleFor(x => x.UserId)
                .NotEmpty()
                .WithMessage("UserId is required.");

            RuleFor(x => x.Role)
                .IsInEnum()
                .WithMessage("Role must be a valid UserRole value.");
        }
    }
}
