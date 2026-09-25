using FluentValidation;

namespace NovaWallet.Infrastructure.Extensions;

/// <summary>
/// Provides extension methods for defining reusable FluentValidation rules.
/// </summary>
public static class ValidationExtensions
{
    /// <summary>
    /// Validates that the trimmed value has a minimum number of characters.
    /// </summary>
    /// <typeparam name="T">The type being validated.</typeparam>
    /// <param name="ruleBuilder">The rule builder for the string property.</param>
    /// <param name="minimumLength">The minimum number of characters required.</param>
    /// <param name="message">The error message</param>
    /// <returns>
    /// A rule builder that can be further configured with additional validation rules.
    /// </returns>
    public static IRuleBuilderOptions<T, string> TrimmedMinimumLength<T>(
    this IRuleBuilder<T, string> ruleBuilder,
    int minimumLength,
    string message)
    {
        return ruleBuilder
            .Must(value => value.Trim().Length >= minimumLength)
            .WithMessage(message);
    }

    /// <summary>
    /// Validates that the trimmed value has exactly the specified number of characters.
    /// </summary>
    /// <typeparam name="T">The type being validated.</typeparam>
    /// <param name="ruleBuilder">The rule builder for the string property.</param>
    /// <param name="length">The exact number of characters required.</param>
    /// <param name="message">The error message</param>
    /// <returns>
    /// A rule builder that can be further configured with additional validation rules.
    /// </returns>
    public static IRuleBuilderOptions<T, string> TrimmedExactLength<T>(
        this IRuleBuilder<T, string> ruleBuilder,
        int length,
        string message)
    {
        return ruleBuilder
            .Must(value => value.Trim().Length == length)
            .WithMessage(message);
    }
}