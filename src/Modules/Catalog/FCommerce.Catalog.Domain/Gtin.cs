using FCommerce.SharedKernel.Persistence;
using FCommerce.SharedKernel.Results;

namespace FCommerce.Catalog.Domain;

/// <summary>GTIN-8/12/13/14 (EAN/UPC) with a valid check digit.</summary>
public sealed record Gtin : IPersistedValue<Gtin, string>
{
    private Gtin(string value) => Value = value;

    public string Value { get; }

    public static Result<Gtin, Error> Parse(string raw)
    {
        var value = raw.Trim();
        if (value.Length is not (8 or 12 or 13 or 14) || !value.All(char.IsAsciiDigit))
        {
            return Error.Validation("gtin.invalid", "GTIN must have 8, 12, 13 or 14 digits.");
        }

        return HasValidCheckDigit(value)
            ? new Gtin(value)
            : Error.Validation("gtin.check-digit", "GTIN check digit is invalid.");
    }

    /// <summary>GS1 mod-10: weights 3 and 1 alternate from the rightmost data digit.</summary>
    private static bool HasValidCheckDigit(string digits)
    {
        var sum = digits[..^1]
            .Reverse()
            .Select((c, i) => (c - '0') * (i % 2 == 0 ? 3 : 1))
            .Sum();
        return (10 - (sum % 10)) % 10 == digits[^1] - '0';
    }

    static Gtin IPersistedValue<Gtin, string>.Rehydrate(string raw) => new(raw);

    string IPersistedValue<Gtin, string>.Dehydrate() => Value;

    public override string ToString() => Value;
}
