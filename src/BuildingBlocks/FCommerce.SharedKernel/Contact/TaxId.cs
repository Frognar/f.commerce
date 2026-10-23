using FCommerce.SharedKernel.Persistence;
using FCommerce.SharedKernel.Results;

namespace FCommerce.SharedKernel.Contact;

/// <summary>
/// Tax identification number, normalized to "{prefix}{number}" without separators: "PL5261040828", "DE123456789".
/// A bare 10-digit number is a Polish NIP; Polish numbers must pass the NIP checksum, others are checked for format only.
/// </summary>
public sealed record TaxId : IPersistedValue<TaxId, string>
{
    private TaxId(string value) => Value = value;

    public string Value { get; }

    public string Prefix => Value[..2];

    public string Number => Value[2..];

    public static Result<TaxId, Error> Parse(string? raw)
    {
        var compact = new string((raw ?? string.Empty).Where(c => !char.IsWhiteSpace(c) && c is not '-' and not '.').ToArray()).ToUpperInvariant();
        var value = compact.Length == 10 && compact.All(char.IsAsciiDigit) ? "PL" + compact : compact;

        if (value.Length is < 4 or > 14 || !char.IsAsciiLetter(value[0]) || !char.IsAsciiLetter(value[1]) || !value[2..].All(char.IsAsciiLetterOrDigit))
        {
            return Error.Validation("tax-id.invalid", $"'{raw}' is not a valid tax identification number.");
        }

        return value.StartsWith("PL", StringComparison.Ordinal) && !IsValidNip(value[2..])
            ? Error.Validation("tax-id.checksum", $"'{raw}' is not a valid Polish NIP.")
            : new TaxId(value);
    }

    /// <summary>NIP: 10 digits, weights 6-5-7-2-3-4-5-6-7, sum mod 11 equals the last digit (10 is never valid).</summary>
    private static bool IsValidNip(string digits)
    {
        if (digits.Length != 10 || !digits.All(char.IsAsciiDigit))
        {
            return false;
        }

        int[] weights = [6, 5, 7, 2, 3, 4, 5, 6, 7];
        var checksum = weights.Select((w, i) => w * (digits[i] - '0')).Sum() % 11;
        return checksum != 10 && checksum == digits[9] - '0';
    }

    static TaxId IPersistedValue<TaxId, string>.Rehydrate(string raw) => new(raw);

    string IPersistedValue<TaxId, string>.Dehydrate() => Value;

    public override string ToString() => Value;
}
