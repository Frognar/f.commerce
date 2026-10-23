using FCommerce.SharedKernel.Imports;
using FCommerce.Application.Messaging;
using FCommerce.Catalog.Contracts;
using FCommerce.Catalog.Domain;
using FCommerce.Catalog.Persistence;
using FCommerce.Infrastructure.Outbox;
using FCommerce.Media.Contracts;
using FCommerce.Application.Results;
using FCommerce.SharedKernel.Messaging;
using FCommerce.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace FCommerce.Catalog.Import;

/// <summary>Validate every item (pure) → reconcile with stored products (pure) → store changes and events in one transaction.</summary>
internal sealed class ImportProductsHandler(
    CatalogDbContext db,
    Outbox<CatalogDbContext> outbox,
    IQueryHandler<FindStoredImages, IReadOnlySet<string>> storedImages,
    TimeProvider time)
    : ICommandHandler<ImportProducts, Result<ImportReport, Error>>
{
    public async Task<Result<ImportReport, Error>> HandleAsync(ImportProducts command, CancellationToken cancellationToken = default)
    {
        var validated = ImportBatch.Validate(command.Products, p => p.Sku, p => Product.Validate(ToUnvalidated(p)));
        var referenced = validated.Valid.SelectMany(v => v.Item.Content.Images.Select(i => i.Value)).Distinct().ToList();
        var stored = referenced.Count == 0
            ? new HashSet<string>()
            : await storedImages.HandleAsync(new FindStoredImages(referenced), cancellationToken);
        var batch = Reconciliation.RequireStoredImages(validated, stored);

        var skus = batch.Valid.Select(v => v.Item.Sku.Value).ToList();
        var rows = await db.Products.Where(p => skus.Contains(p.Sku)).ToDictionaryAsync(p => p.Sku, cancellationToken);

        var changes = batch.Valid
            .Select(v => Reconciliation.Reconcile(rows.GetValueOrDefault(v.Item.Sku.Value)?.ToDomain(), v.Item))
            .ToList();

        var now = time.GetUtcNow();
        foreach (var change in changes)
        {
            Store(change, rows, now);
        }

        return await outbox.EnqueueAll(changes.SelectMany(PublicEvents))
            .MapAsync(async _ =>
            {
                await db.SaveChangesAsync(cancellationToken);
                return ImportReport.From(command.Products.Count, changes, batch.Rejected);
            });
    }

    private void Store(Change<Product> change, Dictionary<string, ProductRow> stored, DateTimeOffset now)
    {
        switch (change)
        {
            case Change<Product>.Created created:
                db.Products.Add(ProductRow.From(created.Current, now));
                break;
            case Change<Product>.Updated updated:
                stored[updated.Current.Sku.Value].Apply(updated.Current, now);
                break;
            case Change<Product>.Unchanged unchanged:
                stored[unchanged.Current.Sku.Value].Version = unchanged.Current.Version.Value;
                break;
        }
    }

    private static IEnumerable<IIntegrationEvent> PublicEvents(Change<Product> change) =>
        change switch
        {
            Change<Product>.Created c => [new ProductChanged(c.Current.Sku.Value, c.Current.Active)],
            Change<Product>.Updated u => [new ProductChanged(u.Current.Sku.Value, u.Current.Active)],
            _ => [],
        };

    private static UnvalidatedProduct ToUnvalidated(ProductData data) =>
        new(
            data.Sku,
            data.Version,
            data.Name,
            data.Description,
            data.CategoryCode,
            data.Unit,
            data.Gtin,
            data.ImageIds,
            data.Attributes?.Select(a => new UnvalidatedAttribute(a.Code, a.Name, a.Value)).ToList(),
            data.Active);
}
