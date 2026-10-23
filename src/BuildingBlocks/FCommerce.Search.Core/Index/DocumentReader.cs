using System.Text.Json;

namespace FCommerce.Search.Core.Index;

/// <summary>Extracts the fields an <see cref="IndexSchema"/> needs from a JSON document (strings, numbers or arrays of them).</summary>
public static class DocumentReader
{
    /// <summary>Null when the document has no usable primary key.</summary>
    public static IndexDocument? Read(IndexSchema schema, ReadOnlyMemory<byte> utf8Json)
    {
        using var json = JsonDocument.Parse(utf8Json);
        var root = json.RootElement;
        var ids = Values(root, schema.PrimaryKey);
        var id = ids.Count > 0 ? ids[0] : null;
        return string.IsNullOrEmpty(id)
            ? null
            : new IndexDocument(
                id,
                [.. schema.SearchableFields.Select(field => Values(root, field))],
                [.. schema.FilterableFields.Select(field => Values(root, field))],
                utf8Json);
    }

    private static IReadOnlyList<string> Values(JsonElement root, string field) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(field, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.Array => [.. value.EnumerateArray().Select(Scalar).OfType<string>()],
                _ => Scalar(value) is { } scalar ? [scalar] : [],
            }
            : [];

    private static string? Scalar(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => element.GetRawText(),
            _ => null,
        };
}
