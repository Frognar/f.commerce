using System.Text;
using FCommerce.SharedKernel.Contact;
using FCommerce.SharedKernel.Imports;
using FCommerce.SharedKernel.Persistence;
using FCommerce.SharedKernel.Results;

namespace FCommerce.Customers.Domain;

/// <summary>Customer identifier from the ERP.</summary>
public sealed record CustomerCode : IPersistedValue<CustomerCode, string>
{
    public const int MaxLength = 64;

    private CustomerCode(string value) => Value = value;

    public string Value { get; }

    public static Result<CustomerCode, Error> Parse(string? raw) =>
        SourceCodes.Parse(raw, MaxLength, "customer-code", "Customer code").Map(value => new CustomerCode(value));

    static CustomerCode IPersistedValue<CustomerCode, string>.Rehydrate(string raw) => new(raw);

    string IPersistedValue<CustomerCode, string>.Dehydrate() => Value;

    public override string ToString() => Value;
}

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

/// <summary>Postal code checked per country (Poland: "00-000", also accepted without the dash).</summary>
public sealed record PostalCode : IPersistedValue<PostalCode, string>
{
    private PostalCode(string value) => Value = value;

    public string Value { get; }

    public static Result<PostalCode, Error> Parse(string? raw, CountryCode country)
    {
        var value = raw?.Trim().ToUpperInvariant() ?? string.Empty;
        if (country.Value == "PL")
        {
            var digits = value.Length == 6 && value[2] == '-' ? value.Remove(2, 1) : value;
            return digits.Length == 5 && digits.All(char.IsAsciiDigit)
                ? new PostalCode($"{digits[..2]}-{digits[2..]}")
                : Error.Validation("postal-code.invalid", "A Polish postal code has the form 00-000.");
        }

        return value.Length is >= 2 and <= 10 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '-')
            ? new PostalCode(value)
            : Error.Validation("postal-code.invalid", $"'{raw}' is not a valid postal code.");
    }

    static PostalCode IPersistedValue<PostalCode, string>.Rehydrate(string raw) => new(raw);

    string IPersistedValue<PostalCode, string>.Dehydrate() => Value;

    public override string ToString() => Value;
}

internal static class Texts
{
    /// <summary>Trimmed, with every run of whitespace replaced by one space.</summary>
    public static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value.Trim())
        {
            if (!char.IsWhiteSpace(c))
            {
                builder.Append(c);
            }
            else if (builder[^1] != ' ')
            {
                builder.Append(' ');
            }
        }

        return builder.ToString();
    }
}
