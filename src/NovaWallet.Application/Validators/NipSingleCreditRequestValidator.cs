using FluentValidation;
using NovaWallet.Application.Dto.Login;

namespace NovaWallet.Application.Validators
{
    public sealed class NipSingleCreditRequestValidator : AbstractValidator<NipSingleCreditRequest>
    {
        public NipSingleCreditRequestValidator()
        {
            // Limits mirror ExternalCreditRequestConfiguration's column definitions exactly,
            // so a payload that would fail the DB CHECK/length constraint is rejected here first.
            RuleFor(x => x.SessionId)
                .NotEmpty()
                .MaximumLength(30);

            RuleFor(x => x.TransactionReference)
                .NotEmpty()
                .MaximumLength(64);

            RuleFor(x => x.AmountKobo)
                .GreaterThan(0);

            RuleFor(x => x.BeneficiaryAccountNumber)
                .NotEmpty()
                .MaximumLength(32);

            RuleFor(x => x.OriginatingAccountNumber)
                .NotEmpty()
                .MaximumLength(32);

            RuleFor(x => x.OriginatingBankCode)
                .NotEmpty()
                .MaximumLength(10);

            RuleFor(x => x.Narration)
                .MaximumLength(200)
                .When(x => !string.IsNullOrEmpty(x.Narration));
        }
    }
}
