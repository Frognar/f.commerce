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
        new("SignedInOnly", features, "Hurtownia Śruba", "#AA3300", default, "biuro@sruba.pl", "+48 600 000 000", default, "DETAL", "accounting");

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
    }

    [Fact]
    public void Reports_every_invalid_field()
    {
        var input = Input() with { CatalogVisibility = "Nobody", StoreName = " ", AccentColor = "red", SupportEmail = "biuro", DefaultContactTemplate = "king" };

        var fields = SettingsRules.Validate(input, Everything, 0).ShouldBeInvalid().Select(e => e.Field);

        Assert.Equal(["catalogVisibility", "storeName", "accentColor", "supportEmail", "defaultContactTemplate"], fields);
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
    }

    [Fact]
    public void Changes_name_what_differs()
    {
        var before = SettingsRules.Validate(Input("customer-accounts"), Everything, 0).ShouldBeValid();
        var after = SettingsRules.Validate(Input("customer-accounts", "documents") with { CatalogVisibility = "Public", StoreName = "Nowa nazwa" }, Everything, 1).ShouldBeValid();

        Assert.Equal(["catalogVisibility", "feature+documents", "branding"], SettingsRules.Changes(before, after));
        Assert.Empty(SettingsRules.Changes(before, before));
    }
}
