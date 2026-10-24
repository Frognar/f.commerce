using System.Text.Json;
using FCommerce.Infrastructure.Serialization;
using FCommerce.SharedKernel.Localization;
using FCommerce.SharedKernel.Monetary;
using FCommerce.SharedKernel.Optional;
using FCommerce.Testing;

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

    [Fact]
    public void Writes_absent_values_as_null_and_present_ones_as_themselves()
    {
        var json = JsonSerializer.Serialize(
            new Labelled("bolt", Maybe<string>.None, new Money(1.5m, Testing.Values.Currency("PLN")), 3),
            PersistenceJson.Options);

        Assert.Equal("""{"name":"bolt","note":null,"price":{"amount":1.5,"currency":"PLN"},"count":3}""", json);
    }

    [Fact]
    public void Reads_documents_stored_before_maybe_null_and_missing_values_are_absent()
    {
        // Written while the fields were nullable: null for absent values; "count" did not exist yet.
        const string stored = """{"name":"bolt","note":null,"price":{"amount":1.5,"currency":"PLN"}}""";

        var labelled = JsonSerializer.Deserialize<Labelled>(stored, PersistenceJson.Options)!;

        Assert.Equal(Maybe<string>.None, labelled.Note);
        Assert.Equal(1.5m, labelled.Price.ShouldBeSome().Amount);
        Assert.Equal(Maybe<int>.None, labelled.Count);
        Assert.Equal(labelled, JsonSerializer.Deserialize<Labelled>(JsonSerializer.Serialize(labelled, PersistenceJson.Options), PersistenceJson.Options));
    }

    private sealed record Labelled(string Name, Maybe<string> Note, Maybe<Money> Price, Maybe<int> Count);
}
