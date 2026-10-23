using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using FCommerce.SharedKernel.Collections;
using FCommerce.SharedKernel.Localization;
using FCommerce.SharedKernel.Monetary;

namespace FCommerce.Infrastructure.Serialization;

internal sealed class CultureCodeConverter : JsonConverter<CultureCode>
{
    public override CultureCode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        CultureCode.Parse(reader.GetString()).Match(c => c, e => throw new JsonException(e.Message));

    public override void Write(Utf8JsonWriter writer, CultureCode value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);

    public override CultureCode ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Read(ref reader, typeToConvert, options);

    public override void WriteAsPropertyName(Utf8JsonWriter writer, CultureCode value, JsonSerializerOptions options) =>
        writer.WritePropertyName(value.Value);
}

internal sealed class CurrencyConverter : JsonConverter<Currency>
{
    public override Currency Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Currency.Parse(reader.GetString()).Match(c => c, e => throw new JsonException(e.Message));

    public override void Write(Utf8JsonWriter writer, Currency value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Code);
}

/// <summary>Serializes <see cref="LocalizedText"/> as a plain object: <c>{"pl": "…", "en": "…"}</c>.</summary>
internal sealed class LocalizedTextConverter : JsonConverter<LocalizedText>
{
    public override LocalizedText Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var translations = JsonSerializer.Deserialize<Dictionary<CultureCode, string>>(ref reader, options)
            ?? throw new JsonException("Localized text must be an object.");
        return LocalizedText.Of(translations);
    }

    public override void Write(Utf8JsonWriter writer, LocalizedText value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value.Translations, options);
}

internal sealed class EquatableArrayConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(EquatableArray<>);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(
            typeof(EquatableArrayConverter<>).MakeGenericType(typeToConvert.GetGenericArguments()[0]))!;

    private sealed class EquatableArrayConverter<T> : JsonConverter<EquatableArray<T>>
    {
        public override EquatableArray<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            new(JsonSerializer.Deserialize<ImmutableArray<T>>(ref reader, options));

        public override void Write(Utf8JsonWriter writer, EquatableArray<T> value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value.AsImmutableArray(), options);
    }
}
