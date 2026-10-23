using FCommerce.SharedKernel.Persistence;
using FCommerce.SharedKernel.Results;

namespace FCommerce.Catalog.Domain;

/// <summary>Absolute http(s) address of a product image hosted outside the platform.</summary>
public sealed record ImageUrl : IPersistedValue<ImageUrl, string>
{
    public const int MaxLength = 2048;

    private ImageUrl(string value) => Value = value;

    public string Value { get; }

    public static Result<ImageUrl, Error> Parse(string? raw) =>
        raw is { Length: <= MaxLength }
        && Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? new ImageUrl(uri.AbsoluteUri)
            : Error.Validation("image-url.invalid", "Image URL must be an absolute http(s) address.");

    static ImageUrl IPersistedValue<ImageUrl, string>.Rehydrate(string raw) => new(raw);

    string IPersistedValue<ImageUrl, string>.Dehydrate() => Value;

    public override string ToString() => Value;
}
