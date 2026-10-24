using FCommerce.Pricing.Domain;
using FCommerce.SharedKernel.Monetary;
using FCommerce.SharedKernel.Optional;
using FCommerce.Testing;

namespace FCommerce.Pricing.Tests;

/// <summary>Who sees which price: viewers × default price list × public prices × where the SKU is priced × currencies.</summary>
public sealed class PriceResolutionTests
{
    private static readonly ActivePriceList Retail = List("DETAL", "PLN");
    private static readonly ActivePriceList Wholesale = List("HURT-A", "PLN");
    private static readonly ActivePriceList Export = List("EXPORT", "EUR");

    private static ActivePriceList List(string code, string currency) =>
        new(PriceListCode.Parse(code).ShouldSucceed(), Currency.Parse(currency).ShouldSucceed());

    private static PriceContent Content(decimal net, bool active = true) =>
        new(PriceTiers.Validate(net, []).ShouldBeValid(), VatRate.Parse(23m).ShouldSucceed(), active);

    private static PricingViewer.Anonymous Anonymous => new();

    private static PricingViewer.SignedIn SignedIn(ActivePriceList? own = null) => new(own is null ? Maybe<ActivePriceList>.None : own);

    private static PricingPolicy Policy(ActivePriceList? defaultList, bool publicPrices = true) =>
        new(defaultList is null ? Maybe<ActivePriceList>.None : defaultList, publicPrices);

    /// <summary>Resolves with prices in the given lists ("DETAL" = 10, "HURT-A" = 8, "EXPORT" = 3).</summary>
    private static string Outcome(PricingViewer viewer, PricingPolicy policy, params string[] pricedIn)
    {
        var prices = new Dictionary<string, PriceContent> { ["DETAL"] = Content(10m), ["HURT-A"] = Content(8m), ["EXPORT"] = Content(3m) };
        var outcome = PriceResolution.Resolve(
            viewer,
            PriceResolution.Sources(viewer, policy),
            code => pricedIn.Contains(code.Value) ? prices.GetValueOrNone(code.Value) : Maybe<PriceContent>.None);
        return outcome switch
        {
            PriceOutcome.Priced priced => $"{priced.List.Code.Value} {priced.Price.Tiers.Base}",
            PriceOutcome.SignInRequired => "sign in",
            _ => "on request",
        };
    }

    [Theory]
    [InlineData(false, true, "sign in")] // no default price list: visitors are asked to sign in
    [InlineData(true, true, "DETAL 10")]
    [InlineData(true, false, "sign in")] // public prices off
    public void Visitors_see_the_default_price_list_only_with_public_prices(bool hasDefault, bool publicPrices, string expected)
    {
        Assert.Equal(expected, Outcome(Anonymous, Policy(hasDefault ? Retail : null, publicPrices), "DETAL"));
    }

    [Fact]
    public void Visitors_are_never_told_on_request()
    {
        Assert.Equal("sign in", Outcome(Anonymous, Policy(Retail), "HURT-A"));
    }

    [Theory]
    [InlineData(new[] { "DETAL" }, true, "DETAL 10")]
    [InlineData(new[] { "DETAL" }, false, "DETAL 10")] // public prices concern only visitors
    [InlineData(new string[0], true, "on request")]
    public void Signed_in_without_an_own_list_see_the_default_one(string[] pricedIn, bool publicPrices, string expected)
    {
        Assert.Equal(expected, Outcome(SignedIn(), Policy(Retail, publicPrices), pricedIn));
        Assert.Equal("on request", Outcome(SignedIn(), Policy(null), pricedIn));
    }

    [Theory]
    [InlineData(new[] { "HURT-A", "DETAL" }, "HURT-A 8")] // own list wins
    [InlineData(new[] { "DETAL" }, "DETAL 10")] // SKU not in the own list: default one
    [InlineData(new[] { "HURT-A" }, "HURT-A 8")]
    [InlineData(new string[0], "on request")]
    public void The_own_list_wins_per_sku_and_falls_back_to_the_default_one(string[] pricedIn, string expected)
    {
        Assert.Equal(expected, Outcome(SignedIn(Wholesale), Policy(Retail), pricedIn));
    }

    [Fact]
    public void No_fallback_to_a_default_list_in_another_currency()
    {
        Assert.Equal("on request", Outcome(SignedIn(Export), Policy(Retail), "DETAL"));
        Assert.Equal("EXPORT 3", Outcome(SignedIn(Export), Policy(Retail), "EXPORT", "DETAL"));
    }

    [Fact]
    public void A_withdrawn_price_falls_back_to_the_default_list()
    {
        var sources = PriceResolution.Sources(SignedIn(Wholesale), Policy(Retail));

        var outcome = PriceResolution.Resolve(SignedIn(Wholesale), sources, code => code.Value == "HURT-A" ? Content(8m, active: false) : Content(10m));

        Assert.Equal(Retail, Assert.IsType<PriceOutcome.Priced>(outcome).List);
    }

    [Fact]
    public void Inactive_customers_and_missing_lists_are_priced_like_customers_without_a_list()
    {
        static Maybe<ActivePriceList> ActiveList(PriceListCode code) => code == Wholesale.Code ? Wholesale : Maybe<ActivePriceList>.None;
        var withList = new CustomerTerms(true, Wholesale.Code);

        Assert.Equal(SignedIn(Wholesale), PriceResolution.Viewer(true, withList, ActiveList));
        Assert.Equal(SignedIn(), PriceResolution.Viewer(true, withList with { Active = false }, ActiveList));
        Assert.Equal(SignedIn(), PriceResolution.Viewer(true, withList with { PriceList = PriceListCode.Parse("STARY").ShouldSucceed() }, ActiveList));
        Assert.Equal(SignedIn(), PriceResolution.Viewer(true, Maybe<CustomerTerms>.None, ActiveList));
        Assert.Equal(Anonymous, PriceResolution.Viewer(false, withList, ActiveList));
    }

    [Fact]
    public void The_own_list_as_default_is_looked_up_once()
    {
        Assert.Equal([Retail], PriceResolution.Sources(SignedIn(Retail), Policy(Retail)));
    }
}
