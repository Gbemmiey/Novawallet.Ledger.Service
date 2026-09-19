namespace NovaWallet.Api.Infrastructure.Extensions.OpenTelemetry
{
    /// <summary>
    /// Provides extension methods for masking sensitive data such as BVNs, NINs, bank account numbers,
    /// phone numbers, email addresses, PINs, OTPs, and more.
    /// </summary>
    /// <remarks>
    /// These methods follow common Nigerian fintech masking patterns while being safe and consistent.
    /// The generic <see cref="Mask"/> method is used as the single source of truth for masking logic.
    /// </remarks>
    public static class SensitiveDataExtensions
    {

        /// <summary>
        /// Generic masking for any sensitive string (PIN, OTP, BVN, NIN, account, phone, email, name, IBAN, etc.).
        /// </summary>
        public static string Mask(
            this string? value,
            int visibleFirst = 0,
            int visibleLast = 4,
            char maskChar = '*',
            int minMaskedChars = 4)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            string s = value!;
            int len = s.Length;

            visibleFirst = Math.Max(0, visibleFirst);
            visibleLast = Math.Max(0, Math.Min(visibleLast, len));

            int visibleTotal = visibleFirst + visibleLast;

            // Very short secrets (PIN/OTP/short codes) → aggressive masking
            if (len <= 8)
            {
                if (visibleFirst == 0 && visibleLast == 0)
                    return new string(maskChar, len);

                // If almost nothing is visible → full mask
                if (visibleTotal <= 2 && len >= 4)
                    return new string(maskChar, len);

                // Otherwise proceed but enforce min masked chars
            }

            if (visibleTotal >= len)
                return s;

            string prefix = visibleFirst > 0 ? s.Substring(0, visibleFirst) : "";
            string suffix = visibleLast > 0 ? s[^visibleLast..] : "";   // modern range syntax

            int middleLen = len - visibleTotal;
            middleLen = Math.Max(middleLen, minMaskedChars);

            string middle = new string(maskChar, middleLen);

            return prefix + middle + suffix;
        }

        // ──────────────────────────────────────────────────────────────
        // Type-specific friendly wrappers (now all use the generic Mask)
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Masks a BVN (11 digits) — shows last 4 digits only
        /// </summary>
        public static string MaskBvn(this string? bvn) =>
            bvn?.Mask(visibleFirst: 0, visibleLast: 4, minMaskedChars: 6) ?? string.Empty;

        /// <summary>
        /// Masks a NIN (11 digits) — shows last 4 digits only (same as BVN convention)
        /// </summary>
        public static string MaskNin(this string? nin) =>
            nin?.Mask(visibleFirst: 0, visibleLast: 4, minMaskedChars: 6) ?? string.Empty;

        /// <summary>
        /// Masks a typical Nigerian bank account number (10 digits).
        /// Shows first 3 + last 4 (common pattern).
        /// </summary>
        public static string MaskAccountNumber(this string? account) =>
            account?.Mask(visibleFirst: 3, visibleLast: 4, minMaskedChars: 4) ?? string.Empty;

        /// <summary>
        /// Masks a Nigerian phone number (usually 0803xxxxxxx or +23480xxxxxxx).
        /// Shows first 4 + last 3 digits after cleaning.
        /// </summary>
        public static string MaskPhoneNumber(this string? phone)
        {
            if (string.IsNullOrWhiteSpace(phone))
                return string.Empty;

            // Clean to digits only
            string digits = new string(phone.Where(char.IsDigit).ToArray());

            if (digits.Length is 0 or 1)
                return phone!;

            // For Nigerian numbers we usually want ~ first 4 + last 3 visible
            return digits.Mask(visibleFirst: 4, visibleLast: 3, minMaskedChars: 4);
        }

        /// <summary>
        /// Masks an email address (shows first few chars of local part + domain).
        /// </summary>
        public static string MaskEmail(this string? email)
        {
            if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
                return email ?? string.Empty;

            var parts = email.Split('@');
            if (parts.Length != 2)
                return email!;

            string local = parts[0];
            string domain = parts[1];

            string maskedLocal = local.Length <= 3
                ? local
                : local[..3] + new string('*', local.Length - 3);

            var domainParts = domain.Split('.');
            if (domainParts.Length < 2)
                return $"{maskedLocal}@{domain}";

            string domainName = domainParts[0];
            string maskedDomainName = domainName.Length <= 3
                ? domainName
                : domainName[..3] + new string('*', domainName.Length - 3);

            return $"{maskedLocal}@{maskedDomainName}.{string.Join(".", domainParts[1..])}";
        }


        /// <summary>
        /// Masks IBAN (international bank account) — preserves country code + check digits (first 4 chars) + last 4 digits.
        /// Common secure pattern for display/logs (15–34 chars depending on country).
        /// </summary>
        public static string MaskIban(this string? iban) =>
            iban?.Mask(visibleFirst: 4, visibleLast: 4, minMaskedChars: 8) ?? string.Empty;

        /// <summary>
        /// Masks credit/debit card number — first 6 + last 4 visible (standard PCI-like pattern).
        /// Works on strings with/without spaces/dashes.
        /// </summary>
        public static string MaskCreditCard(this string? card) =>
            card?.Mask(visibleFirst: 6, visibleLast: 4, minMaskedChars: 6) ?? string.Empty;

        /// <summary>
        /// Masks passport number — first 3 chars (usually letters) + last 4 digits visible.
        /// Adjust visibleFirst if your passports use different formats.
        /// </summary>
        public static string MaskPassportNumber(this string? passport) =>
            passport?.Mask(visibleFirst: 3, visibleLast: 4, minMaskedChars: 6) ?? string.Empty;

        /// <summary>
        /// Generic national ID masking (fallback for other countries' IDs) — last 4 visible.
        /// </summary>
        public static string MaskNationalId(this string? id) =>
            id?.Mask(visibleFirst: 0, visibleLast: 4, minMaskedChars: 6) ?? string.Empty;
    }
}