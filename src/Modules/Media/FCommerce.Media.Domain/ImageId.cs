using FCommerce.SharedKernel.Persistence;
using FCommerce.SharedKernel.Results;

namespace FCommerce.Media.Domain;

/// <summary>Content-derived image identifier: lowercase hex SHA-256 of the original file (64 characters).</summary>
public sealed record ImageId : IPersistedValue<ImageId, string>
{
    public const int Length = 64;

    private ImageId(string value) => Value = value;

    public string Value { get; }

    public static Result<ImageId, Error> Parse(string raw)
    {
        var value = raw.Trim().ToLowerInvariant();
        return value.Length == Length && value.All(char.IsAsciiHexDigitLower)
            ? new ImageId(value)
            : Error.Validation("image-id.invalid", $"'{raw}' is not a valid image id.");
    }

    /// <summary>Storage key prefix, sharded by the first two characters to keep directories small.</summary>
    public string StoragePrefix => $"images/{Value[..2]}/{Value}";

    static ImageId IPersistedValue<ImageId, string>.Rehydrate(string raw) => new(raw);

    string IPersistedValue<ImageId, string>.Dehydrate() => Value;

    public override string ToString() => Value;
}
