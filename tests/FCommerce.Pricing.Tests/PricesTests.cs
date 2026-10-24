using FCommerce.Pricing.Domain;
using FCommerce.SharedKernel.Imports;
using FCommerce.Testing;

namespace FCommerce.Pricing.Tests;

public sealed class PricesTests
{
    private static UnvalidatedPrice Price(decimal net = 10m, params PriceBreak[] breaks) => new(" SR-M8 ", 1, net, 23m, breaks, Active: true);

    private static VatRate Vat(decimal percent) => VatRate.Parse(percent).ShouldSucceed();

    [Fact]
    public void A_valid_price_list_is_normalized()
    {
        var list = Prices.Validate(new UnvalidatedPriceList(" DETAL ", 3, "Cennik detaliczny", "pln", Active: true)).ShouldBeValid();

        Assert.Equal(("DETAL", 3L, "PLN"), (list.Code.Value, list.Version.Value, list.Content.Currency.Code));
    }

    [Fact]
    public void A_price_list_reports_every_invalid_field()
    {
        var fields = Prices.Validate(new UnvalidatedPriceList(" ", -1, "", "złoty", Active: true)).ShouldBeInvalid().Select(e => e.Field.ValueOr(""));

        Assert.Equal(["code", "version", "name", "currency"], fields);
    }

    [Fact]
    public void A_valid_price_has_its_tiers()
    {
        var price = Prices.Validate(Price(10m, new PriceBreak(50, 9.5m), new PriceBreak(200, 9.1234m))).ShouldBeValid();

        Assert.Equal("SR-M8", price.Sku);
        Assert.Equal(10m, price.Content.Tiers.Base);
        Assert.Equal([new PriceBreak(1, 10m), new PriceBreak(50, 9.5m), new PriceBreak(200, 9.1234m)], price.Content.Tiers.All);
        Assert.Equal(23m, price.Content.Vat.Percent);
    }

    [Theory]
    [InlineData(0, "net-price.not-positive")]
    [InlineData(-1, "net-price.not-positive")]
    [InlineData(0.00001, "net-price.precision")]
    [InlineData(100000000000, "net-price.too-large")]
    public void Net_prices_are_positive_with_at_most_four_decimal_places(decimal net, string code)
    {
        var errors = Prices.Validate(Price(net)).ShouldBeInvalid();

        Assert.Equal((code, "netPrice"), (Assert.Single(errors).Code, errors[0].Field.ValueOr("")));
    }

    [Theory]
    [InlineData(-1, "vat-rate.out-of-range")]
    [InlineData(101, "vat-rate.out-of-range")]
    [InlineData(23.555, "vat-rate.precision")]
    public void Vat_rates_are_percentages_with_at_most_two_decimal_places(decimal vat, string code)
    {
        var errors = Prices.Validate(Price() with { VatRate = vat }).ShouldBeInvalid();

        Assert.Equal((code, "vatRate"), (Assert.Single(errors).Code, errors[0].Field.ValueOr("")));
    }

    [Fact]
    public void Breaks_apply_above_one_unit_in_strictly_ascending_quantities()
    {
        var fromOne = Prices.Validate(Price(10m, new PriceBreak(1, 9m))).ShouldBeInvalid();
        var descending = Prices.Validate(Price(10m, new PriceBreak(100, 9m), new PriceBreak(50, 9.5m))).ShouldBeInvalid();
        var duplicate = Prices.Validate(Price(10m, new PriceBreak(50, 9m), new PriceBreak(50, 8m))).ShouldBeInvalid();
        var tooMany = Prices.Validate(Price(10m, [.. Enumerable.Range(2, 21).Select(q => new PriceBreak(q, 9m))])).ShouldBeInvalid();
        var badItem = Prices.Validate(Price(10m, new PriceBreak(50.0001m, 0m))).ShouldBeInvalid();

        Assert.Equal(("break.from-quantity", "breaks[0].fromQuantity"), (Assert.Single(fromOne).Code, fromOne[0].Field.ValueOr("")));
        Assert.Equal("breaks.not-ascending", Assert.Single(descending).Code);
        Assert.Equal("breaks.not-ascending", Assert.Single(duplicate).Code);
        Assert.Equal("breaks.too-many", Assert.Single(tooMany).Code);
        Assert.Equal(["breaks[0].fromQuantity", "breaks[0].netPrice"], badItem.Select(e => e.Field.ValueOr("")));
    }

    [Theory]
    [InlineData(0.5, 10)]
    [InlineData(1, 10)]
    [InlineData(99, 9.5)]
    [InlineData(100, 9)]
    [InlineData(1000, 9)]
    public void The_tier_is_the_last_one_the_quantity_reaches(decimal quantity, decimal net)
    {
        var tiers = Prices.Validate(Price(10m, new PriceBreak(50, 9.5m), new PriceBreak(100, 9m))).ShouldBeValid().Content.Tiers;

        Assert.Equal(net, tiers.For(quantity).Net);
    }

    [Theory]
    [InlineData(10.00, 23, 12.30)]
    [InlineData(10.01, 23, 12.31)]
    [InlineData(0.0123, 23, 0.0151)]
    [InlineData(0.125, 8, 0.135)]
    [InlineData(10.00, 0, 10.00)]
    [InlineData(0.05, 23, 0.06)]
    public void Gross_is_rounded_half_away_from_zero_to_the_places_of_the_net_price(decimal net, decimal vat, decimal gross)
    {
        Assert.Equal(gross, Prices.GrossUnit(net, Vat(vat)));
    }

    [Fact]
    public void Reconciling_prices_follows_the_import_pattern()
    {
        var stored = Prices.Validate(Price()).ShouldBeValid();
        var newer = Prices.Validate(Price() with { Version = 2 }).ShouldBeValid();
        var changed = Prices.Validate(Price(11m) with { Version = 2 }).ShouldBeValid();
        var withdrawn = Prices.Validate(Price() with { Version = 2, Active = false }).ShouldBeValid();

        Assert.IsType<Change<Price>.Created>(Prices.Reconcile(default, stored));
        Assert.IsType<Change<Price>.Stale>(Prices.Reconcile(stored, stored));
        Assert.IsType<Change<Price>.Unchanged>(Prices.Reconcile(stored, newer));
        Assert.IsType<Change<Price>.Updated>(Prices.Reconcile(stored, changed));
        Assert.IsType<Change<Price>.Updated>(Prices.Reconcile(stored, withdrawn));
    }
}
