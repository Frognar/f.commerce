using FCommerce.SharedKernel.Persistence;
using FCommerce.SharedKernel.Results;

namespace FCommerce.SharedKernel.Monetary;

/// <summary>ISO 4217 currency code, e.g. "PLN", "EUR". Created only through <see cref="Parse"/>.</summary>
public sealed record Currency : IPersistedValue<Currency, string>
{
    private Currency(string code) => Code = code;

    public string Code { get; }

    public static Result<Currency, Error> Parse(string? code) =>
        code is { Length: 3 } && code.All(char.IsAsciiLetter)
            ? new Currency(code.ToUpperInvariant())
            : Error.Validation("currency.invalid", $"'{code}' is not a valid ISO 4217 currency code.");

    static Currency IPersistedValue<Currency, string>.Rehydrate(string raw) => new(raw);

    string IPersistedValue<Currency, string>.Dehydrate() => Code;

    public override string ToString() => Code;
}
