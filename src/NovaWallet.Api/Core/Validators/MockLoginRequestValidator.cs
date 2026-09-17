using FluentValidation;
using NovaWallet.Api.Core.Dto.Login;

namespace NovaWallet.Api.Core.Validators
{
    public sealed class MockLoginRequestValidator : AbstractValidator<MockLoginRequest>
    {
        public MockLoginRequestValidator()
        {
            RuleFor(x => x.UserId)
                .NotEmpty()
                .WithMessage("UserId is required.");
        }
    }
}
