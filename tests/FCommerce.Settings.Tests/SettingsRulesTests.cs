using System.Collections.Immutable;
using FCommerce.Authorization.Core;
using FCommerce.Settings.Domain;
using FCommerce.SharedKernel.Features;
using FCommerce.Testing;

namespace FCommerce.Settings.Tests;

public sealed class SettingsRulesTests
{
    private static readonly ImmutableHashSet<FeatureId> Everything = [.. Feature.All.Select(f => f.Id)];

    private static UnvalidatedSettings Input(params string[] features) =>
        new("SignedInOnly", features, "Hurtownia Śruba", "#AA3300", default, "biuro@sruba.pl", "+48 600 000 000", default, "DETAL", "accounting", "Exact", 2.5m);

    [Fact]
    public void Valid_input_becomes_the_next_version()
    {
        var settings = SettingsRules.Validate(Input("customer-accounts", "documents"), Everything, currentVersion: 4).ShouldBeValid();

        Assert.Equal(5, settings.Version);
        Assert.Equal(CatalogVisibility.SignedInOnly, settings.CatalogVisibility);
        Assert.Equal("#aa3300", settings.Branding.AccentColor.Value);
        Assert.Equal("biuro@sruba.pl", settings.Contact.SupportEmail.ShouldBeSome().Value);
        Assert.Equal(("DETAL", "accounting"), (settings.Commerce.DefaultPriceListCode.ShouldBeSome(), settings.Commerce.DefaultContactTemplate));
        Assert.True(settings.Features(Everything).IsOn(Feature.Documents));
        Assert.Equal(new StockDisplay(StockDisplayMode.Exact, 2.5m), settings.Stock);
    }

    [Fact]
    public void Reports_every_invalid_field()
    {
        var input = Input() with
        {
            CatalogVisibility = "Nobody", StoreName = " ", AccentColor = "red", SupportEmail = "biuro", DefaultContactTemplate = "king",
            StockDisplay = "Rounded", LowStockThreshold = -1,
        };

        var fields = SettingsRules.Validate(input, Everything, 0).ShouldBeInvalid().Select(e => e.Field);

        Assert.Equal(["catalogVisibility", "storeName", "accentColor", "supportEmail", "defaultContactTemplate", "stockDisplay", "lowStockThreshold"], fields);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(0.125, true)]
    [InlineData(2.5000, true)]
    [InlineData(0.0005, false)]
    [InlineData(-0.001, false)]
    public void Low_stock_threshold_has_at_most_three_decimal_places_and_is_not_negative(decimal threshold, bool valid)
    {
        var result = SettingsRules.Validate(Input() with { LowStockThreshold = threshold }, Everything, 0);

        Assert.Equal(valid, result.Match(_ => true, _ => false));
    }

    [Fact]
    public void Features_must_be_available_and_have_their_requirements_enabled()
    {
        var withoutRegistration = Everything.Remove(Feature.SelfRegistration);

        var unavailable = SettingsRules.Validate(Input("customer-accounts", "self-registration"), withoutRegistration, 0).ShouldBeInvalid();
        var missingRequirement = SettingsRules.Validate(Input("documents"), Everything, 0).ShouldBeInvalid();
        var unknown = SettingsRules.Validate(Input("teleport"), Everything, 0).ShouldBeInvalid();

        Assert.Equal("feature.unavailable", Assert.Single(unavailable).Code);
        Assert.Equal("feature.missing-requirement", Assert.Single(missingRequirement).Code);
        Assert.Equal("feature.unknown", Assert.Single(unknown).Code);
    }

    [Fact]
    public void Defaults_enable_only_default_features_available_here()
    {
        var defaults = PlatformSettings.Defaults(Everything.Remove(Feature.Documents));

        Assert.Equal(CatalogVisibility.Public, defaults.CatalogVisibility);
        Assert.DoesNotContain(Feature.Documents, defaults.EnabledFeatures);
        Assert.DoesNotContain(Feature.SelfRegistration, defaults.EnabledFeatures); // off by default
        Assert.Contains(Feature.CustomerAccounts, defaults.EnabledFeatures);
        Assert.Equal(new StockDisplay(StockDisplayMode.Levels, 10), defaults.Stock);
    }

    [Fact]
    public void Changes_name_what_differs()
    {
        var before = SettingsRules.Validate(Input("customer-accounts"), Everything, 0).ShouldBeValid();
        var after = SettingsRules.Validate(Input("customer-accounts", "documents") with { CatalogVisibility = "Public", StoreName = "Nowa nazwa" }, Everything, 1).ShouldBeValid();

        Assert.Equal(["catalogVisibility", "feature+documents", "branding"], SettingsRules.Changes(before, after));
        Assert.Equal(["stock"], SettingsRules.Changes(before, before with { Stock = StockDisplay.Default }));
        Assert.Empty(SettingsRules.Changes(before, before));
    }

    [Fact]
    public void Features_added_since_saving_get_their_default_state_and_known_ones_keep_the_sellers_choice()
    {
        ImmutableHashSet<FeatureId> known = [.. Everything.Except([Feature.PublicPrices, Feature.StockLevels, Feature.SelfRegistration])];

        var enabled = SettingsRules.WithNewFeatures([Feature.CustomerAccounts], known, Everything.Remove(Feature.StockLevels));

        Assert.Equal([Feature.CustomerAccounts, Feature.PublicPrices], Feature.All.Select(f => f.Id).Where(enabled.Contains));
    }

    [Fact]
    public void A_new_feature_whose_requirement_is_switched_off_stays_off()
    {
        ImmutableHashSet<FeatureId> known = [.. Everything.Remove(Feature.Documents)];

        Assert.DoesNotContain(Feature.Documents, SettingsRules.WithNewFeatures([], known, Everything));
        Assert.Contains(Feature.Documents, SettingsRules.WithNewFeatures([Feature.CustomerAccounts], known, Everything));
    }
}
