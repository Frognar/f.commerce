using FCommerce.SharedKernel.Persistence;
using FCommerce.SharedKernel.Results;

namespace FCommerce.SharedKernel.Contact;

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
