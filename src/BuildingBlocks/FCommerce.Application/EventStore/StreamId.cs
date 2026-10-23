using FCommerce.SharedKernel.Results;

namespace FCommerce.Application.EventStore;

/// <summary>
/// An event-sourced aggregate type. Its <see cref="Category"/> is a constant checked by the architecture tests
/// (format and uniqueness), so building a <see cref="StreamId"/> from it needs no validation at runtime.
/// </summary>
public interface IStreamType
{
    /// <summary>Lowercase ASCII letters and digits, starting with a letter, e.g. "cart", "order".</summary>
    static abstract string Category { get; }
}

/// <summary>Stream identifier in the form "{category}-{id}", e.g. "cart-0193…".</summary>
public sealed record StreamId
{
    public const string ForeignCategory = "$foreign";

    private const int MaxCategoryLength = 50;

    private StreamId(string category, string id)
    {
        Category = category;
        Id = id;
    }

    public string Category { get; }

    public string Id { get; }

    public string Value => $"{Category}-{Id}";

    public static StreamId For<TStream>(Guid id)
        where TStream : IStreamType =>
        new(TStream.Category, id.ToString("N"));

    /// <summary>Parses a stored key "{category}-{id}", splitting at the first '-'.</summary>
    public static Result<StreamId, Error> Parse(string? value)
    {
        var separator = value?.IndexOf('-', StringComparison.Ordinal) ?? -1;
        if (separator <= 0 || separator == value!.Length - 1)
        {
            return Error.Validation("stream-id.invalid", $"'{value}' is not a valid stream id.");
        }

        var category = value[..separator];
        return ValidateCategory(category).Map(_ => new StreamId(category, value[(separator + 1)..]));
    }

    public static Result<string, Error> ValidateCategory(string? category) =>
        category is { Length: > 0 and <= MaxCategoryLength }
        && char.IsAsciiLetterLower(category[0])
        && category.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c))
            ? category
            : Error.Validation("stream-id.invalid-category", $"'{category}' is not a valid stream category (lowercase letters and digits, starting with a letter).");

    /// <summary>
    /// Wraps a stream key that does not follow our naming (e.g. an adapter's own bookkeeping streams) so it can
    /// still be passed through the global log. No <see cref="IStreamType"/> can have the <see cref="ForeignCategory"/>.
    /// </summary>
    public static StreamId Foreign(string? rawKey) =>
        new(ForeignCategory, string.IsNullOrWhiteSpace(rawKey) ? "unknown" : rawKey);

    public override string ToString() => Value;
}
