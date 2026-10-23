using FCommerce.SharedKernel.Results;

namespace FCommerce.Application.EventStore;

/// <summary>
/// Stream identifier in the form "{category}-{id}", e.g. "cart-0193…".
/// The category names the aggregate type and is owned by exactly one module.
/// </summary>
public sealed record StreamId
{
    private StreamId(string category, string id)
    {
        Category = category;
        Id = id;
    }

    public string Category { get; }

    public string Id { get; }

    public string Value => $"{Category}-{Id}";

    public static Result<StreamId, Error> Create(string category, string id)
    {
        if (string.IsNullOrWhiteSpace(category) || category.Contains('-', StringComparison.Ordinal))
        {
            return Error.Validation("stream-id.invalid-category", $"'{category}' is not a valid stream category (non-empty, without '-').");
        }

        return string.IsNullOrWhiteSpace(id)
            ? Error.Validation("stream-id.empty-id", "Stream id must not be empty.")
            : new StreamId(category, id);
    }

    public static Result<StreamId, Error> Create(string category, Guid id) => Create(category, id.ToString("N"));

    /// <summary>Splits "{category}-{id}" at the first '-'.</summary>
    public static Result<StreamId, Error> Parse(string? value)
    {
        var separator = value?.IndexOf('-', StringComparison.Ordinal) ?? -1;
        return separator > 0
            ? Create(value![..separator], value[(separator + 1)..])
            : Error.Validation("stream-id.invalid", $"'{value}' is not a valid stream id.");
    }

    /// <summary>
    /// Wraps a stream key that does not follow our naming (e.g. an adapter's own bookkeeping streams) so it can
    /// still be passed through the global log. Subscriptions never accept the <see cref="ForeignCategory"/>.
    /// </summary>
    public static StreamId Foreign(string? rawKey) =>
        new(ForeignCategory, string.IsNullOrWhiteSpace(rawKey) ? "unknown" : rawKey);

    public const string ForeignCategory = "$foreign";

    public override string ToString() => Value;
}
