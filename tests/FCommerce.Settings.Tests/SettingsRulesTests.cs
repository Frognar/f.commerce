using System.Collections.Immutable;
using FCommerce.Authorization.Core;
using FCommerce.Settings.Domain;
using FCommerce.SharedKernel.Features;
using FCommerce.SharedKernel.Validation;
using FCommerce.Testing;

namespace FCommerce.Settings.Tests;

public sealed class SettingsRulesTests
{
    private static readonly ImmutableHashSet<FeatureId> Everything = [.. Feature.All.Select(f => f.Id)];

    private static readonly PlatformSettings Initial = PlatformSettings.Defaults(Everything);

    private static UnvalidatedSettings Input(params string[] features) =>
        new("SignedInOnly", features, "Hurtownia Śruba", "#AA3300", default, "biuro@sruba.pl", "+48 600 000 000", default, "DETAL", "accounting", "Exact", 2.5m);

    private static Validated<PlatformSettings> Validate(UnvalidatedSettings input) => Validate(input, Everything);

    private static Validated<PlatformSettings> Validate(UnvalidatedSettings input, ImmutableHashSet<FeatureId> available) =>
        SettingsRules.Validate(input, available, Initial);

    [Fact]
    public void Valid_input_becomes_the_next_version()
    {
        var first = Validate(Input("customer-accounts")).ShouldBeValid();
        var settings = SettingsRules.Validate(Input("customer-accounts", "documents"), Everything, first).ShouldBeValid();

        Assert.Equal((1L, 2L), (first.Version, settings.Version));
        Assert.Equal(CatalogVisibility.SignedInOnly, settings.CatalogVisibility);
        Assert.Equal("#aa3300", settings.Branding.AccentColor.Value);
        Assert.Equal("biuro@sruba.pl", settings.Contact.SupportEmail.ShouldBeSome().Value);
        Assert.Equal(("DETAL", "accounting"), (settings.Commerce.DefaultPriceList.ShouldBeSome().Value, settings.Commerce.DefaultContactTemplate.Value));
        Assert.True(settings.Features(Everything).IsOn(Feature.Documents));
        Assert.Equal((StockDisplayMode.Exact, 2.5m), (settings.Stock.Mode, settings.Stock.LowThreshold.Value));
    }

    [Fact]
    public void A_save_over_settings_changed_meanwhile_is_a_conflict()
    {
        var saved = SettingsRules.Update(Initial, 0, Input("customer-accounts"), Everything).ShouldSucceed();

        Assert.Equal(1, saved.Version);
        Assert.Equal("settings.changed-meanwhile", SettingsRules.Update(saved, 0, Input(), Everything).ShouldFail().Code);
    }

    [Fact]
    public void Reports_every_invalid_field()
    {
        var input = Input() with
        {
            CatalogVisibility = "Nobody", StoreName = " ", AccentColor = "red", SupportEmail = "biuro", DefaultContactTemplate = "king",
            StockDisplay = "Rounded", LowStockThreshold = -1,
        };

        var fields = Validate(input).ShouldBeInvalid().Select(e => e.Field);

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
        var result = Validate(Input() with { LowStockThreshold = threshold });

        Assert.Equal(valid, result.Match(_ => true, _ => false));
    }

    [Fact]
    public void Features_must_be_available_and_have_their_requirements_enabled()
    {
        var withoutRegistration = Everything.Remove(Feature.SelfRegistration);

        var unavailable = Validate(Input("customer-accounts", "self-registration"), withoutRegistration).ShouldBeInvalid();
        var missingRequirement = Validate(Input("documents")).ShouldBeInvalid();
        var unknown = Validate(Input("teleport")).ShouldBeInvalid();

        Assert.Equal("feature.unavailable", Assert.Single(unavailable).Code);
        Assert.Equal("feature.missing-requirement", Assert.Single(missingRequirement).Code);
        Assert.Equal("feature.unknown", Assert.Single(unknown).Code);
    }

    [Fact]
    public void Defaults_enable_only_default_features_available_here()
    {
        var defaults = PlatformSettings.Defaults(Everything.Remove(Feature.Documents));

        Assert.Equal(CatalogVisibility.Public, defaults.CatalogVisibility);
        Assert.DoesNotContain(Feature.Documents, defaults.EnabledFeatures.Ids);
        Assert.DoesNotContain(Feature.SelfRegistration, defaults.EnabledFeatures.Ids); // off by default
        Assert.Contains(Feature.CustomerAccounts, defaults.EnabledFeatures.Ids);
        Assert.Equal(StockDisplay.Default, defaults.Stock);
        Assert.Equal((StockDisplayMode.Levels, 10m), (defaults.Stock.Mode, defaults.Stock.LowThreshold.Value));
    }

    [Fact]
    public void Changes_name_what_differs()
    {
        var before = Validate(Input("customer-accounts")).ShouldBeValid();
        var after = SettingsRules.Validate(Input("customer-accounts", "documents") with { CatalogVisibility = "Public", StoreName = "Nowa nazwa" }, Everything, before).ShouldBeValid();
        var levels = SettingsRules.Validate(Input("customer-accounts") with { StockDisplay = "Levels" }, Everything, before).ShouldBeValid();

        Assert.Equal(["catalogVisibility", "feature+documents", "branding"], SettingsRules.Changes(before, after));
        Assert.Equal(["stock"], SettingsRules.Changes(before, levels));
        Assert.Empty(SettingsRules.Changes(before, before));
    }

    [Fact]
    public void Features_added_since_saving_get_their_default_state_and_known_ones_keep_the_sellers_choice()
    {
        ImmutableHashSet<FeatureId> known = [.. Everything.Except([Feature.PublicPrices, Feature.StockLevels, Feature.SelfRegistration])];

        var enabled = EnabledFeatures.Restore([Feature.CustomerAccounts], known, Everything.Remove(Feature.StockLevels));

        Assert.Equal([Feature.CustomerAccounts, Feature.PublicPrices], Feature.All.Select(f => f.Id).Where(enabled.Contains));
    }

    [Fact]
    public void A_new_feature_whose_requirement_is_switched_off_stays_off()
    {
        ImmutableHashSet<FeatureId> known = [.. Everything.Remove(Feature.Documents)];

        Assert.DoesNotContain(Feature.Documents, EnabledFeatures.Restore([], known, Everything).Ids);
        Assert.Contains(Feature.Documents, EnabledFeatures.Restore([Feature.CustomerAccounts], known, Everything).Ids);
    }

    [Fact]
    public void A_feature_whose_requirement_is_no_longer_available_is_off()
    {
        var enabled = EnabledFeatures.Restore([Feature.CustomerAccounts, Feature.Documents], [.. Everything], Everything.Remove(Feature.CustomerAccounts));

        Assert.Empty(enabled.Ids);
    }
}
