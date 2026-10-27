using System.Collections.Immutable;
using FCommerce.Infrastructure.Persistence;
using FCommerce.Settings.Domain;
using FCommerce.Settings.Persistence;
using FCommerce.SharedKernel.Features;
using FCommerce.Testing;

namespace FCommerce.Settings.Tests;

public sealed class SettingsDocumentTests
{
    private static readonly ImmutableHashSet<FeatureId> Everything = [.. Feature.All.Select(f => f.Id)];

    /// <summary>A document saved before stock display and the list of known features were stored.</summary>
    private const string SavedBeforeStock = """
        {"catalogVisibility":"Public","enabledFeatures":["customer-accounts","documents"],"storeName":"Hurtownia","accentColor":"#1f5eff",
         "logoImageId":null,"supportEmail":null,"supportPhone":null,"operatorNotifications":null,"defaultPriceListCode":"DETAL","defaultContactTemplate":"buyer"}
        """;

    [Fact]
    public void An_older_document_reads_with_default_stock_display_and_new_features_on()
    {
        var settings = SettingsDocument.Read(SavedBeforeStock, 3, Everything);

        Assert.Equal(StockDisplay.Default, settings.Stock);
        // The two stored features, plus every feature on by default that the code gained since (M3 and later).
        Assert.Equal(
            [Feature.CustomerAccounts, Feature.Documents, .. Feature.All.Skip(6).Where(f => f.EnabledByDefault).Select(f => f.Id)],
            Feature.All.Select(f => f.Id).Where(settings.EnabledFeatures.Contains));
        Assert.Contains(Feature.PublicPrices, settings.EnabledFeatures.Ids);
        Assert.DoesNotContain(Feature.SelfRegistration, settings.EnabledFeatures.Ids);
    }

    [Fact]
    public void Features_switched_off_after_they_were_known_stay_off()
    {
        var before = SettingsDocument.Read(SavedBeforeStock, 3, Everything);
        var input = new UnvalidatedSettings("Public", ["customer-accounts"], "Hurtownia", "#1f5eff", default, default, default, default, "DETAL", "buyer", "Exact", 0.5m);
        var saved = SettingsDocument.Write(SettingsRules.Validate(input, Everything, before).ShouldBeValid());

        var settings = SettingsDocument.Read(saved, 4, Everything);

        Assert.Equal([Feature.CustomerAccounts], settings.EnabledFeatures.Ids);
        Assert.Equal((StockDisplayMode.Exact, 0.5m), (settings.Stock.Mode, settings.Stock.LowThreshold.Value));
        Assert.Equal("DETAL", settings.Commerce.DefaultPriceList.ShouldBeSome().Value);
    }

    [Theory]
    [InlineData("\"accentColor\":\"#1f5eff\"", "\"accentColor\":\"red\"")]
    [InlineData("\"defaultContactTemplate\":\"buyer\"", "\"defaultContactTemplate\":\"buyer\",\"stockDisplay\":\"Exact\"")]
    [InlineData("\"defaultContactTemplate\":\"buyer\"", "\"defaultContactTemplate\":\"buyer\",\"stockDisplay\":\"Exact\",\"lowStockThreshold\":-1")]
    public void A_document_that_cannot_be_valid_settings_is_corrupt(string field, string replacement)
    {
        Assert.Throws<StoredDataException>(() => SettingsDocument.Read(SavedBeforeStock.Replace(field, replacement, StringComparison.Ordinal), 3, Everything));
    }
}
