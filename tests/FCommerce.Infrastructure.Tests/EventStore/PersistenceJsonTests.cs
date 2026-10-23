using System.Text.Json;
using FCommerce.Infrastructure.Serialization;
using FCommerce.SharedKernel.Localization;
using FCommerce.SharedKernel.Monetary;

namespace FCommerce.Infrastructure.Tests.EventStore;

public sealed class PersistenceJsonTests
{
    [Fact]
    public void Writes_persisted_values_in_their_raw_form()
    {
        var json = JsonSerializer.Serialize(
            new ThingDescribed(
                LocalizedText.Of((Testing.Values.Culture("pl"), "Sruba M8")),
                [],
                new Money(1.5m, Testing.Values.Currency("PLN")),
                Testing.Values.Culture("en-GB")),
            PersistenceJson.Options);

        Assert.Equal("""{"description":{"pl":"Sruba M8"},"tags":[],"price":{"amount":1.5,"currency":"PLN"},"culture":"en-GB"}""", json);
    }

    [Fact]
    public void Reads_stored_values_without_revalidating_them()
    {
        // Values that today's Parse rejects: stored facts must stay readable when rules get stricter.
        const string stored = """{"description":{"xx-YYYYYY":"?"},"tags":[],"price":{"amount":1,"currency":"PLNX"},"culture":"toolong-REGION"}""";

        var described = JsonSerializer.Deserialize<ThingDescribed>(stored, PersistenceJson.Options)!;

        Assert.Equal("PLNX", described.Price.Currency.Code);
        Assert.Equal("toolong-REGION", described.Culture.Value);
        Assert.Equal("xx-YYYYYY", Assert.Single(described.Description.Translations).Key.Value);
        Assert.True(Currency.Parse("PLNX").Match(_ => false, _ => true));
    }
}
