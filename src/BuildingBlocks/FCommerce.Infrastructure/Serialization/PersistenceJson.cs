using System.Text.Json;
using System.Text.Json.Serialization;

namespace FCommerce.Infrastructure.Serialization;

/// <summary>
/// The JSON configuration for persisted data (events, outbox messages). Every event store adapter must use it.
/// Not for HTTP input: requests carry primitives that are parsed explicitly, never rehydrated.
/// </summary>
public static class PersistenceJson
{
    public static JsonSerializerOptions Options { get; } = Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    public static JsonSerializerOptions Configure(JsonSerializerOptions options)
    {
        options.Converters.Add(new PersistedValueConverterFactory());
        options.Converters.Add(new EquatableArrayConverterFactory());
        options.Converters.Add(new MaybeConverterFactory());
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
