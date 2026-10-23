using FCommerce.SharedKernel.Results;

namespace FCommerce.SharedKernel.Monetary;

/// <summary>An amount in a currency. Arithmetic across currencies is an error value.</summary>
public sealed record Money(decimal Amount, Currency Currency)
{
    public static Money Zero(Currency currency) => new(0m, currency);

    public Result<Money, Error> Add(Money other) =>
        other.Currency == Currency
            ? this with { Amount = Amount + other.Amount }
            : Error.Validation("money.currency-mismatch", $"Cannot add {other.Currency} to {Currency}.");

    public Money Multiply(decimal factor) => this with { Amount = Amount * factor };

    /// <summary>Rounds half away from zero (commercial rounding) to the given number of decimals.</summary>
    public Money Round(int decimals = 2) => this with { Amount = Math.Round(Amount, decimals, MidpointRounding.AwayFromZero) };

    public override string ToString() => $"{Amount:0.00##} {Currency}";
}
