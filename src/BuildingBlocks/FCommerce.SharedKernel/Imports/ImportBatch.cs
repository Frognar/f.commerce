using FCommerce.SharedKernel.Collections;
using FCommerce.SharedKernel.Results;
using FCommerce.SharedKernel.Validation;

namespace FCommerce.SharedKernel.Imports;

/// <summary>What applying an incoming record from a source system (ERP) to the stored one means.</summary>
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
public sealed record ValidatedBatch<T>(EquatableArray<(int Index, T Item)> Valid, EquatableArray<Rejection> Rejected)
{
    /// <summary>
    /// Applies a contextual check to the valid items (e.g. "referenced images exist"); items with errors move to the
    /// rejected ones, which stay ordered by input index.
    /// </summary>
    public ValidatedBatch<T> Require(Func<T, EquatableArray<Error>> check, Func<T, string?> key)
    {
        var checkedItems = Valid.Select(v => (v.Index, v.Item, Errors: check(v.Item))).ToList();
        return new ValidatedBatch<T>(
            checkedItems.Where(c => c.Errors.IsEmpty).Select(c => (c.Index, c.Item)).ToEquatableArray(),
            Rejected
                .Concat(checkedItems.Where(c => !c.Errors.IsEmpty).Select(c => new Rejection(c.Index, key(c.Item), c.Errors)))
                .OrderBy(r => r.Index)
                .ToEquatableArray());
    }
}

/// <summary>The import pattern shared by every module fed from a source system.</summary>
public static class ImportBatch
{
    /// <summary>
    /// Validates every item of a batch independently (one bad item does not reject the batch) and rejects all
    /// occurrences of a key that appears more than once.
    /// </summary>
    public static ValidatedBatch<T> Validate<TInput, T>(
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

    /// <summary>Created when new, ignored when not newer, unchanged when only the version moved, otherwise updated.</summary>
    public static Change<T> Reconcile<T>(T? stored, T incoming, Func<T, SourceVersion> version, Func<T, T, bool> sameContent)
        where T : class =>
        stored switch
        {
            null => new Change<T>.Created(incoming),
            _ when version(incoming) <= version(stored) => new Change<T>.Stale(stored, incoming),
            _ when sameContent(stored, incoming) => new Change<T>.Unchanged(incoming),
            _ => new Change<T>.Updated(stored, incoming),
        };

    /// <summary>Changes that buyers can see (created or updated), for publishing events.</summary>
    public static bool IsVisible<T>(this Change<T> change) => change is Change<T>.Created or Change<T>.Updated;

    /// <summary>The record to store for a change; null when nothing is stored (stale).</summary>
    public static T? Current<T>(this Change<T> change)
        where T : class =>
        change switch
        {
            Change<T>.Created c => c.Current,
            Change<T>.Updated u => u.Current,
            Change<T>.Unchanged u => u.Current,
            _ => null,
        };
}
