using FCommerce.SharedKernel.Collections;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Results;
using FCommerce.SharedKernel.Validation;

namespace FCommerce.SharedKernel.Imports;

/// <summary>What applying an incoming record from a source system (ERP) to the stored one means.</summary>
public abstract record Change<T>
    where T : notnull
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

/// <summary>A rejected input item and why; the key is absent when the item had none.</summary>
public sealed record Rejection(int Index, Maybe<string> Key, EquatableArray<Error> Errors);

/// <summary>Valid items ready to reconcile and items rejected by validation.</summary>
public sealed record ValidatedBatch<T>(EquatableArray<(int Index, T Item)> Valid, EquatableArray<Rejection> Rejected)
    where T : notnull
{
    /// <summary>
    /// Applies a contextual check to the valid items (e.g. "referenced images exist"); items with errors move to the
    /// rejected ones, which stay ordered by input index.
    /// </summary>
    public ValidatedBatch<T> Require(Func<T, EquatableArray<Error>> check, Func<T, string> key)
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
    /// occurrences of a key that appears more than once. <paramref name="rawKey"/> is the key as received
    /// (absent when the item has none). An item that is not there at all (null in the JSON of the request) is
    /// rejected like any invalid item.
    /// </summary>
    public static ValidatedBatch<T> Validate<TInput, T>(
        IReadOnlyList<TInput> inputs,
        Func<TInput, Maybe<string>> rawKey,
        Func<TInput, Validated<T>> validate)
        where TInput : notnull
        where T : notnull
    {
        // Deserialized lists may hold nulls although the contract types say otherwise.
        Maybe<TInput> Present(TInput input) => input is null ? Maybe<TInput>.None : Maybe.Some(input);

        Maybe<string> KeyOf(TInput input) => Present(input).Bind(rawKey).Map(key => key.Trim());

        var duplicates = inputs
            .Choose(KeyOf)
            .GroupBy(key => key, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);

        var outcomes = inputs.Select((input, index) =>
        {
            var key = KeyOf(input);
            var validated = key.Where(duplicates.Contains).Match(
                duplicate => Validated.Invalid<T>(Error.Conflict("batch.duplicate-key", $"'{duplicate}' occurs more than once in the batch.")),
                () => Present(input).Match(
                    validate,
                    () => Validated.Invalid<T>(Error.Validation("batch.item-missing", "The item is empty."))));
            return validated.Match(
                item => Result.Ok<(int Index, T Item), Rejection>((index, item)),
                errors => Result.Fail<(int Index, T Item), Rejection>(new Rejection(index, key, errors)));
        }).ToList();

        return new ValidatedBatch<T>(
            outcomes.Choose(o => o.ToMaybe()).ToEquatableArray(),
            outcomes.Choose(o => o.Match(_ => Maybe<Rejection>.None, rejection => rejection)).ToEquatableArray());
    }

    /// <summary>Created when new, ignored when not newer, unchanged when only the version moved, otherwise updated.</summary>
    public static Change<T> Reconcile<T>(Maybe<T> stored, T incoming, Func<T, SourceVersion> version, Func<T, T, bool> sameContent)
        where T : notnull =>
        stored.Match<Change<T>>(
            current => version(incoming) <= version(current) ? new Change<T>.Stale(current, incoming)
                : sameContent(current, incoming) ? new Change<T>.Unchanged(incoming)
                : new Change<T>.Updated(current, incoming),
            () => new Change<T>.Created(incoming));

    /// <summary>Changes that buyers can see (created or updated), for publishing events.</summary>
    public static bool IsVisible<T>(this Change<T> change)
        where T : notnull =>
        change is Change<T>.Created or Change<T>.Updated;

    /// <summary>The record to store for a change; none when nothing is stored (stale).</summary>
    public static Maybe<T> Current<T>(this Change<T> change)
        where T : notnull =>
        change switch
        {
            Change<T>.Created c => c.Current,
            Change<T>.Updated u => u.Current,
            Change<T>.Unchanged u => u.Current,
            _ => Maybe<T>.None,
        };
}
