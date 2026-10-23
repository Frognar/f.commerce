using System.Text.Json;
using System.Text.Json.Serialization;

namespace FCommerce.Infrastructure.Serialization;

/// <summary>The single JSON configuration for persisted events and messages (any event store adapter must use it).</summary>
public static class JsonDefaults
{
    public static JsonSerializerOptions Options { get; } = Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    public static JsonSerializerOptions Configure(JsonSerializerOptions options)
    {
        options.Converters.Add(new CultureCodeConverter());
        options.Converters.Add(new CurrencyConverter());
        options.Converters.Add(new LocalizedTextConverter());
        options.Converters.Add(new EquatableArrayConverterFactory());
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
