using System.Text.Json;
using FCommerce.SharedKernel.Optional;

namespace FCommerce.Search.Core.Index;

/// <summary>Extracts the fields an <see cref="IndexSchema"/> needs from a JSON document (strings, numbers or arrays of them).</summary>
public static class DocumentReader
{
    /// <summary>None when the document has no usable primary key.</summary>
    public static Maybe<IndexDocument> Read(IndexSchema schema, ReadOnlyMemory<byte> utf8Json)
    {
        using var json = JsonDocument.Parse(utf8Json);
        var root = json.RootElement;
        return Values(root, schema.PrimaryKey)
            .FirstOrNone()
            .Where(id => id.Length > 0)
            .Map(id => new IndexDocument(
                id,
                [.. schema.SearchableFields.Select(field => Values(root, field))],
                [.. schema.FilterableFields.Select(field => Values(root, field))],
                utf8Json));
    }

    private static IReadOnlyList<string> Values(JsonElement root, string field) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(field, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.Array => [.. value.EnumerateArray().Choose(Scalar)],
                _ => [.. Scalar(value).AsEnumerable()],
            }
            : [];

    private static Maybe<string> Scalar(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => element.GetRawText(),
            _ => Maybe<string>.None,
        };
}
