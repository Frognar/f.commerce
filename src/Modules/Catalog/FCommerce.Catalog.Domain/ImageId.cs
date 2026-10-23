using FCommerce.SharedKernel.Persistence;
using FCommerce.SharedKernel.Results;

namespace FCommerce.Catalog.Domain;

/// <summary>Reference to an image in the media store (content-derived id: 64 lowercase hex characters).</summary>
public sealed record ImageId : IPersistedValue<ImageId, string>
{
    public const int Length = 64;

    private ImageId(string value) => Value = value;

    public string Value { get; }

    public static Result<ImageId, Error> Parse(string? raw)
    {
        var value = raw?.Trim().ToLowerInvariant();
        return value is { Length: Length } && value.All(char.IsAsciiHexDigitLower)
            ? new ImageId(value)
            : Error.Validation("image-id.invalid", "Image id must be the id returned by the media upload.");
    }

    static ImageId IPersistedValue<ImageId, string>.Rehydrate(string raw) => new(raw);

    string IPersistedValue<ImageId, string>.Dehydrate() => Value;

    public override string ToString() => Value;
}
