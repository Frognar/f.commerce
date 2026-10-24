using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using FCommerce.SharedKernel.Collections;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Persistence;

namespace FCommerce.Infrastructure.Serialization;

/// <summary>Writes any <see cref="IPersistedValue{TSelf,TRaw}"/> as its raw form and reads it back without re-validation.</summary>
internal sealed class PersistedValueConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => RawTypeOf(typeToConvert).Match(_ => true, () => false);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(
            typeof(PersistedValueConverter<,>).MakeGenericType(typeToConvert, RawTypeOf(typeToConvert).ValueOr(typeof(object))))!;

    private static Maybe<Type> RawTypeOf(Type type) =>
        type.GetInterfaces()
            .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IPersistedValue<,>) && i.GenericTypeArguments[0] == type)
            .Select(i => i.GenericTypeArguments[1])
            .FirstOrNone();

    private sealed class PersistedValueConverter<T, TRaw> : JsonConverter<T>
        where T : IPersistedValue<T, TRaw>
        where TRaw : notnull
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            T.Rehydrate(JsonSerializer.Deserialize<TRaw>(ref reader, options) ?? throw new JsonException($"Missing value for {typeof(T).Name}."));

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value.Dehydrate(), options);
    }
}

/// <summary>Writes <see cref="Maybe{T}.None"/> as <c>null</c> and a present value as itself (the shape stored before Maybe).</summary>
internal sealed class MaybeConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(Maybe<>);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(
            typeof(MaybeConverter<>).MakeGenericType(typeToConvert.GetGenericArguments()[0]))!;

    private sealed class MaybeConverter<T> : JsonConverter<Maybe<T>>
        where T : notnull
    {
        public override bool HandleNull => true;

        public override Maybe<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.Null
                ? Maybe<T>.None
                : JsonSerializer.Deserialize<T>(ref reader, options) is { } value ? value : Maybe<T>.None;

        public override void Write(Utf8JsonWriter writer, Maybe<T> value, JsonSerializerOptions options) =>
            value.Switch(present => JsonSerializer.Serialize(writer, present, options), writer.WriteNullValue);
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
        where T : notnull
    {
        public override EquatableArray<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            new(JsonSerializer.Deserialize<ImmutableArray<T>>(ref reader, options));

        public override void Write(Utf8JsonWriter writer, EquatableArray<T> value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value.AsImmutableArray(), options);
    }
}
