using FCommerce.SharedKernel.Results;

namespace FCommerce.SharedKernel.Imports;

/// <summary>Response of every bulk import of the integration API.</summary>
public sealed record ImportReport(
    int Received,
    int Created,
    int Updated,
    int Unchanged,
    int Stale,
    IReadOnlyList<RejectedItem> Rejected)
{
    public static ImportReport From<T>(int received, IReadOnlyCollection<Change<T>> changes, IEnumerable<Rejection> rejected) =>
        new(
            received,
            changes.Count(c => c is Change<T>.Created),
            changes.Count(c => c is Change<T>.Updated),
            changes.Count(c => c is Change<T>.Unchanged),
            changes.Count(c => c is Change<T>.Stale),
            [.. rejected.Select(r => new RejectedItem(r.Index, r.Key, [.. r.Errors.SelectMany(Flatten)]))]);

    private static IEnumerable<FieldError> Flatten(Error error) =>
        error.Details.IsEmpty
            ? [new FieldError(error.Field, error.Code, error.Message)]
            : error.Details.SelectMany(Flatten);
}

/// <param name="Index">Position of the item in the request.</param>
public sealed record RejectedItem(int Index, string? Key, IReadOnlyList<FieldError> Errors);

/// <param name="Field">Path of the offending input, e.g. "name.pl" or "attributes[2].code".</param>
public sealed record FieldError(string? Field, string Code, string Message);
