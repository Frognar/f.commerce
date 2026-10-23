using FCommerce.SharedKernel.Persistence;
using FCommerce.SharedKernel.Results;

namespace FCommerce.SharedKernel.Localization;

/// <summary>A normalized BCP 47 culture tag, e.g. "pl", "en", "en-GB". Created only through <see cref="Parse"/>.</summary>
public sealed record CultureCode : IPersistedValue<CultureCode, string>
{
    private CultureCode(string language, string? region)
    {
        LanguagePart = language;
        Region = region;
    }

    private string LanguagePart { get; }

    private string? Region { get; }

    public string Value => Region is null ? LanguagePart : $"{LanguagePart}-{Region}";

    /// <summary>The neutral language part: "en-GB" → "en".</summary>
    public CultureCode Language => Region is null ? this : new CultureCode(LanguagePart, null);

    public bool IsNeutral => Region is null;

    public static Result<CultureCode, Error> Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Error.Validation("culture.empty", "Culture code is required.");
        }

        var parts = value.Trim().Replace('_', '-').Split('-');
        var valid = parts.Length <= 2
            && parts[0].Length is >= 2 and <= 3
            && parts[0].All(char.IsAsciiLetter)
            && (parts.Length == 1 || (parts[1].Length is >= 2 and <= 4 && parts[1].All(char.IsAsciiLetterOrDigit)));

        return valid
            ? new CultureCode(parts[0].ToLowerInvariant(), parts.Length == 2 ? parts[1].ToUpperInvariant() : null)
            : Error.Validation("culture.invalid", $"'{value}' is not a valid culture code.");
    }

    static CultureCode IPersistedValue<CultureCode, string>.Rehydrate(string raw)
    {
        var separator = raw.IndexOf('-', StringComparison.Ordinal);
        return separator < 0 ? new CultureCode(raw, null) : new CultureCode(raw[..separator], raw[(separator + 1)..]);
    }

    string IPersistedValue<CultureCode, string>.Dehydrate() => Value;

    public override string ToString() => Value;
}
