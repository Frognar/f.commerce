using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using FCommerce.SharedKernel.Collections;
using FCommerce.SharedKernel.Persistence;

namespace FCommerce.Infrastructure.Serialization;

/// <summary>Writes any <see cref="IPersistedValue{TSelf,TRaw}"/> as its raw form and reads it back without re-validation.</summary>
internal sealed class PersistedValueConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => RawTypeOf(typeToConvert) is not null;

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(
            typeof(PersistedValueConverter<,>).MakeGenericType(typeToConvert, RawTypeOf(typeToConvert)!))!;

    private static Type? RawTypeOf(Type type) =>
        type.GetInterfaces()
            .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IPersistedValue<,>) && i.GenericTypeArguments[0] == type)
            .Select(i => i.GenericTypeArguments[1])
            .FirstOrDefault();

    private sealed class PersistedValueConverter<T, TRaw> : JsonConverter<T>
        where T : IPersistedValue<T, TRaw>
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            T.Rehydrate(JsonSerializer.Deserialize<TRaw>(ref reader, options) ?? throw new JsonException($"Missing value for {typeof(T).Name}."));

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value.Dehydrate(), options);
    }
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
