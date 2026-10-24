using FCommerce.SharedKernel.Collections;
using FCommerce.SharedKernel.Monetary;
using FCommerce.Testing;
using static FCommerce.Testing.Values;

namespace FCommerce.SharedKernel.Tests;

public sealed class EquatableArrayTests
{
    private sealed record Holder(string Name, EquatableArray<int> Values);

    [Fact]
    public void Records_holding_arrays_compare_by_value()
    {
        Assert.Equal(new Holder("a", EquatableArray.Create(1, 2)), new Holder("a", EquatableArray.Create(1, 2)));
        Assert.NotEqual(new Holder("a", EquatableArray.Create(1, 2)), new Holder("a", EquatableArray.Create(2, 1)));
    }

    [Fact]
    public void Default_value_behaves_as_empty()
    {
        EquatableArray<int> value = default;

        Assert.True(value.IsEmpty);
        Assert.Equal(EquatableArray<int>.Empty, value);
    }

    [Fact]
    public void Collection_expressions_build_the_array()
    {
        EquatableArray<int> values = [1, 2, 3];
        int[] more = [4, 5];
        EquatableArray<int> spread = [0, .. more];

        Assert.Equal(EquatableArray.Create(1, 2, 3), values);
        Assert.Equal(EquatableArray.Create(0, 4, 5), spread);
    }

    [Fact]
    public void Changes_return_new_arrays()
    {
        var original = EquatableArray.Create(1, 2);

        var added = original.Add(3);

        Assert.Equal(EquatableArray.Create(1, 2), original);
        Assert.Equal(EquatableArray.Create(1, 2, 3), added);
    }
}

public sealed class MoneyTests
{
    private static readonly Currency Pln = Currency("PLN");
    private static readonly Currency Eur = Currency("EUR");

    [Fact]
    public void Adds_amounts_in_the_same_currency()
    {
        Assert.Equal(new Money(3.5m, Pln), new Money(1.25m, Pln).Add(new Money(2.25m, Pln)).ShouldSucceed());
    }

    [Fact]
    public void Adding_different_currencies_is_an_error_value()
    {
        Assert.Equal("money.currency-mismatch", new Money(1m, Pln).Add(new Money(1m, Eur)).ShouldFail().Code);
    }

    [Theory]
    [InlineData(1.005, 1.01)]
    [InlineData(1.004, 1.00)]
    [InlineData(-1.005, -1.01)]
    public void Rounds_half_away_from_zero(decimal amount, decimal expected)
    {
        Assert.Equal(expected, new Money(amount, Pln).Round().Amount);
    }

    [Theory]
    [InlineData("pln", "PLN")]
    [InlineData("Eur", "EUR")]
    public void Normalizes_currency_codes(string input, string expected)
    {
        Assert.Equal(expected, Monetary.Currency.Parse(input).ShouldSucceed().Code);
    }

    [Theory]
    [InlineData("PL")]
    [InlineData("PLN1")]
    [InlineData("P1N")]
    public void Rejects_invalid_currency_codes(string input)
    {
        Assert.Equal("currency.invalid", Monetary.Currency.Parse(input).ShouldFail().Code);
    }
}
