using System.Collections.Immutable;
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
        Assert.Equal(
            [Feature.CustomerAccounts, Feature.Documents, Feature.PublicPrices, Feature.StockLevels],
            Feature.All.Select(f => f.Id).Where(settings.EnabledFeatures.Contains));
    }

    [Fact]
    public void Features_switched_off_after_they_were_known_stay_off()
    {
        var saved = SettingsDocument.Write(SettingsDocument.Read(SavedBeforeStock, 3, Everything) with
        {
            EnabledFeatures = [Feature.CustomerAccounts],
            Stock = new StockDisplay(StockDisplayMode.Exact, 0.5m),
        });

        var settings = SettingsDocument.Read(saved, 4, Everything);

        Assert.Equal([Feature.CustomerAccounts], settings.EnabledFeatures);
        Assert.Equal(new StockDisplay(StockDisplayMode.Exact, 0.5m), settings.Stock);
        Assert.Equal("DETAL", settings.Commerce.DefaultPriceListCode.ShouldBeSome());
    }
}
