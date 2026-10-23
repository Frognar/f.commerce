using FCommerce.SharedKernel.Collections;
using FCommerce.SharedKernel.Results;
using FCommerce.SharedKernel.Validation;

namespace FCommerce.Catalog.Domain;

/// <summary>What applying an incoming ERP record to the stored one means.</summary>
public abstract record Change<T>
{
    private Change()
    {
    }

    public sealed record Created(T Current) : Change<T>;

    public sealed record Updated(T Previous, T Current) : Change<T>;

    /// <summary>Same content under a newer version: store the version, publish nothing.</summary>
    public sealed record Unchanged(T Current) : Change<T>;

    /// <summary>Not newer than what is stored: ignored.</summary>
    public sealed record Stale(T Stored, T Incoming) : Change<T>;
}

/// <summary>A rejected input item and why.</summary>
public sealed record Rejection(int Index, string? Key, EquatableArray<Error> Errors);

/// <summary>Valid items ready to reconcile and items rejected by validation.</summary>
public sealed record ValidatedBatch<T>(EquatableArray<(int Index, T Item)> Valid, EquatableArray<Rejection> Rejected);

public static class Reconciliation
{
    /// <summary>
    /// Validates every item of an import batch independently (one bad item does not reject the batch) and
    /// rejects all occurrences of a key that appears more than once.
    /// </summary>
    public static ValidatedBatch<T> ValidateBatch<TInput, T>(
        IReadOnlyList<TInput> inputs,
        Func<TInput, string?> rawKey,
        Func<TInput, Validated<T>> validate)
    {
        var duplicates = inputs
            .GroupBy(input => rawKey(input)?.Trim(), StringComparer.Ordinal)
            .Where(group => group.Key is not null && group.Count() > 1)
            .Select(group => group.Key!)
            .ToHashSet(StringComparer.Ordinal);

        var outcomes = inputs.Select((input, index) =>
        {
            var key = rawKey(input)?.Trim();
            var validated = key is not null && duplicates.Contains(key)
                ? Validated.Invalid<T>(Error.Conflict("batch.duplicate-key", $"'{key}' occurs more than once in the batch."))
                : validate(input);
            return validated.Match(
                item => (Valid: (Index: index, Item: item), Rejected: (Rejection?)null),
                errors => (Valid: default, Rejected: new Rejection(index, key, errors)));
        }).ToList();

        return new ValidatedBatch<T>(
            outcomes.Where(o => o.Rejected is null).Select(o => o.Valid).ToEquatableArray(),
            outcomes.Where(o => o.Rejected is not null).Select(o => o.Rejected!).ToEquatableArray());
    }

    /// <summary>
    /// Contextual check: every referenced image must already be in the media store (<paramref name="storedImages"/>).
    /// Products referencing missing images move to the rejected items.
    /// </summary>
    public static ValidatedBatch<Product> RequireStoredImages(ValidatedBatch<Product> batch, IReadOnlySet<string> storedImages)
    {
        var missing = batch.Valid
            .Select(v => (v.Index, v.Item, Errors: v.Item.Content.Images
                .Select((image, position) => (image, position))
                .Where(i => !storedImages.Contains(i.image.Value))
                .Select(i => Error.Validation("image.not-uploaded", $"Image '{i.image}' has not been uploaded.") with { Field = $"imageIds[{i.position}]" })
                .ToEquatableArray()))
            .ToList();

        return new ValidatedBatch<Product>(
            missing.Where(m => m.Errors.IsEmpty).Select(m => (m.Index, m.Item)).ToEquatableArray(),
            batch.Rejected
                .Concat(missing.Where(m => !m.Errors.IsEmpty).Select(m => new Rejection(m.Index, m.Item.Sku.Value, m.Errors)))
                .OrderBy(r => r.Index)
                .ToEquatableArray());
    }

    public static Change<Product> Reconcile(Product? stored, Product incoming) =>
        Reconcile(stored, incoming, p => p.Version, (a, b) => a.Content == b.Content && a.Active == b.Active);

    public static Change<Category> Reconcile(Category? stored, Category incoming) =>
        Reconcile(stored, incoming, c => c.Version, (a, b) => a.Content == b.Content && a.Active == b.Active);

    private static Change<T> Reconcile<T>(T? stored, T incoming, Func<T, SourceVersion> version, Func<T, T, bool> sameContent)
        where T : class =>
        stored switch
        {
            null => new Change<T>.Created(incoming),
            _ when version(incoming) <= version(stored) => new Change<T>.Stale(stored, incoming),
            _ when sameContent(stored, incoming) => new Change<T>.Unchanged(incoming),
            _ => new Change<T>.Updated(stored, incoming),
        };
}
