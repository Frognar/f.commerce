using FCommerce.Catalog.Contracts;
using FCommerce.Catalog.Domain;
using FCommerce.SharedKernel.Results;

namespace FCommerce.Catalog.Import;

internal static class ImportReports
{
    public static ImportReport Create<T>(int received, IReadOnlyList<Change<T>> changes, IEnumerable<Rejection> rejected) =>
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
