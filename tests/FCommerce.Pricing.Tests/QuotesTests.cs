using FCommerce.Pricing.Domain;
using FCommerce.SharedKernel.Monetary;
using FCommerce.Testing;

namespace FCommerce.Pricing.Tests;

public sealed class QuotesTests
{
    private static readonly Currency Pln = Currency.Parse("PLN").ShouldSucceed();
    private static readonly Currency Eur = Currency.Parse("EUR").ShouldSucceed();

    private static PriceContent Price(decimal net, decimal vat = 23m, params PriceBreak[] breaks) =>
        new(PriceTiers.Validate(net, breaks).ShouldBeValid(), VatRate.Parse(vat).ShouldSucceed(), Active: true);

    [Fact]
    public void A_line_is_priced_at_the_tier_of_its_quantity_and_rounded_to_two_places()
    {
        var line = Quotes.Line(Pln, Price(0.0123m, 23m, new PriceBreak(1000, 0.0101m)), 1500);
        var small = Quotes.Line(Pln, Price(0.0123m, 23m, new PriceBreak(1000, 0.0101m)), 7);

        Assert.Equal((0.0101m, 0.0124m, 15.15m), (line.UnitNet, line.UnitGross, line.NetAmount));
        Assert.Equal(0.09m, small.NetAmount); // 0.0861 rounds half away from zero
    }

    [Fact]
    public void Vat_is_calculated_per_rate_from_the_sum_of_net_values()
    {
        // Per line VAT would be 0.23 + 0.23 + 0.23 = 0.69; from the sum of 3.03 it is 0.70.
        var lines = new[] { Quotes.Line(Pln, Price(1.01m), 1), Quotes.Line(Pln, Price(1.01m), 1), Quotes.Line(Pln, Price(1.01m), 1), Quotes.Line(Pln, Price(10m, 8m), 2) };

        var total = Assert.Single(Quotes.Total(lines));

        Assert.Equal([new RateTotal(VatRate.Parse(8m).ShouldSucceed(), 20m, 1.60m), new RateTotal(VatRate.Parse(23m).ShouldSucceed(), 3.03m, 0.70m)], total.Rates);
        Assert.Equal((23.03m, 2.30m, 25.33m), (total.Net, total.VatAmount, total.Gross));
    }

    [Fact]
    public void Totals_are_kept_per_currency()
    {
        var totals = Quotes.Total([Quotes.Line(Pln, Price(10m), 1), Quotes.Line(Eur, Price(2m, 0m), 3)]);

        Assert.Equal([("EUR", 6m, 6m), ("PLN", 10m, 12.30m)], totals.Select(t => (t.Currency.Code, t.Net, t.Gross)));
        Assert.Empty(Quotes.Total([]));
    }
}
