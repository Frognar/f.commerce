using FCommerce.SharedKernel.Imports;
using FCommerce.Application.Messaging;
using FCommerce.Catalog.Contracts;
using FCommerce.Catalog.Domain;
using FCommerce.Catalog.Persistence;
using FCommerce.Catalog.Search;
using FCommerce.Infrastructure.Outbox;
using FCommerce.Application.Results;
using FCommerce.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace FCommerce.Catalog.Import;

internal sealed class ImportCategoriesHandler(CatalogDbContext db, Outbox<CatalogDbContext> outbox, TimeProvider time)
    : ICommandHandler<ImportCategories, Result<ImportReport, Error>>
{
    public async Task<Result<ImportReport, Error>> HandleAsync(ImportCategories command, CancellationToken cancellationToken = default)
    {
        var batch = ImportBatch.Validate(
            command.Categories,
            c => c.Code,
            c => Category.Validate(new UnvalidatedCategory(c.Code, c.ParentCode, c.Name, c.SortOrder, c.Version, c.Active)));

        var codes = batch.Valid.Select(v => v.Item.Code.Value).ToList();
        var stored = await db.Categories.Where(c => codes.Contains(c.Code)).ToDictionaryAsync(c => c.Code, cancellationToken);

        var changes = batch.Valid
            .Select(v => Reconciliation.Reconcile(stored.GetValueOrDefault(v.Item.Code.Value)?.ToDomain(), v.Item))
            .ToList();

        var now = time.GetUtcNow();
        foreach (var change in changes)
        {
            switch (change)
            {
                case Change<Category>.Created created:
                    db.Categories.Add(CategoryRow.From(created.Current, now));
                    break;
                case Change<Category>.Updated updated:
                    stored[updated.Current.Code.Value].Apply(updated.Current, now);
                    break;
                case Change<Category>.Unchanged unchanged:
                    stored[unchanged.Current.Code.Value].Version = unchanged.Current.Version.Value;
                    break;
            }
        }

        var treeChanged = changes.Any(c => c is Change<Category>.Created or Change<Category>.Updated);
        return await (treeChanged ? outbox.Enqueue(new CategoriesChanged()) : Unit.Value)
            .MapAsync(async _ =>
            {
                await db.SaveChangesAsync(cancellationToken);
                return ImportReport.From(command.Categories.Count, changes, batch.Rejected);
            });
    }
}
