using FCommerce.Application.Messaging;
using FCommerce.Application.Results;
using FCommerce.Inventory.Contracts;
using FCommerce.Inventory.Domain;
using FCommerce.Inventory.Persistence;
using FCommerce.Settings.Contracts;
using FCommerce.SharedKernel.Contracts;
using FCommerce.SharedKernel.Features;
using FCommerce.SharedKernel.Imports;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Results;
using FCommerce.SharedKernel.Validation;
using Microsoft.EntityFrameworkCore;

namespace FCommerce.Inventory;

/// <summary>"Today" for deliveries: the date in the seller's time zone.</summary>
internal sealed class InventoryClock(TimeProvider time, InventoryOptions options)
{
    private readonly TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById(options.TimeZone);

    public DateTimeOffset Now => time.GetUtcNow();

    public DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), zone).DateTime);
}

/// <summary>Validate every item (pure) → reconcile with stored warehouses (pure) → store the changes.</summary>
internal sealed class ImportWarehousesHandler(InventoryDbContext db, InventoryClock clock) : ICommandHandler<ImportWarehouses, Result<ImportReport, Error>>
{
    public async Task<Result<ImportReport, Error>> HandleAsync(ImportWarehouses command, CancellationToken cancellationToken = default)
    {
        var batch = ImportBatch.Validate(
            command.Warehouses,
            w => w.Code.ToMaybe(),
            w => Stock.Validate(new UnvalidatedWarehouse(w.Code.OrEmpty(), w.Version, w.Name.OrEmpty(), w.Visible, w.SortOrder, w.Active)));

        var codes = batch.Valid.Select(v => v.Item.Code.Value).ToList();
        var rows = await db.Warehouses.Where(w => codes.Contains(w.Code)).ToDictionaryAsync(w => w.Code, cancellationToken);
        var changes = batch.Valid
            .Select(v => Stock.Reconcile(rows.GetValueOrNone(v.Item.Code.Value).Map(row => row.ToDomain()), v.Item))
            .ToList();

        foreach (var change in changes)
        {
            switch (change)
            {
                case Change<Warehouse>.Created created:
                    db.Warehouses.Add(WarehouseRow.From(created.Current, clock.Now));
                    break;
                case Change<Warehouse>.Updated updated:
                    rows[updated.Current.Code.Value].Apply(updated.Current, clock.Now);
                    break;
                case Change<Warehouse>.Unchanged unchanged:
                    rows[unchanged.Current.Code.Value].Version = unchanged.Current.Version.Value;
                    break;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return ImportReport.From(command.Warehouses.Count, changes, batch.Rejected);
    }
}

/// <summary>Stock of one imported warehouse, keyed by SKU; the same pattern as the warehouses.</summary>
internal sealed class ImportStockHandler(InventoryDbContext db, InventoryClock clock) : ICommandHandler<ImportStock, Result<ImportReport, Error>>
{
    public Task<Result<ImportReport, Error>> HandleAsync(ImportStock command, CancellationToken cancellationToken = default) =>
        WarehouseCode.Parse(command.WarehouseCode.OrEmpty()).BindAsync(async code =>
            await db.Warehouses.AnyAsync(w => w.Code == code.Value, cancellationToken)
                ? Result.Ok<ImportReport, Error>(await ImportAsync(code, command.Stock, cancellationToken))
                : Error.NotFound("warehouse.not-found", $"Warehouse '{code}' has not been imported."));

    private async Task<ImportReport> ImportAsync(WarehouseCode code, IReadOnlyList<StockData> stock, CancellationToken cancellationToken)
    {
        var batch = ImportBatch.Validate(
            stock,
            s => s.Sku.ToMaybe(),
            s => Stock.Validate(new UnvalidatedStock(s.Sku.OrEmpty(), s.Version, s.Quantity, s.NextDeliveryDate.ToMaybe(), s.NextDeliveryQuantity.ToMaybe())));

        var skus = batch.Valid.Select(v => v.Item.Sku.Value).ToList();
        var rows = await db.Stock.Where(s => s.WarehouseCode == code.Value && skus.Contains(s.Sku)).ToDictionaryAsync(s => s.Sku, cancellationToken);
        var changes = batch.Valid
            .Select(v => Stock.Reconcile(rows.GetValueOrNone(v.Item.Sku.Value).Map(row => row.ToDomain()), v.Item))
            .ToList();

        foreach (var change in changes)
        {
            switch (change)
            {
                case Change<StockEntry>.Created created:
                    db.Stock.Add(StockRow.From(code, created.Current, clock.Now));
                    break;
                case Change<StockEntry>.Updated updated:
                    rows[updated.Current.Sku.Value].Apply(updated.Current, clock.Now);
                    break;
                case Change<StockEntry>.Unchanged unchanged:
                    rows[unchanged.Current.Sku.Value].Version = unchanged.Current.Version.Value;
                    break;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return ImportReport.From(stock.Count, changes, batch.Rejected);
    }
}

/// <summary>How stock is shown, from the settings; none when the "stock-levels" feature is off.</summary>
internal static class StockSettings
{
    public static async Task<Maybe<StockPolicy>> PolicyAsync(IQueryHandler<GetPlatformSettings, PlatformSettingsView> settings, CancellationToken cancellationToken)
    {
        var platform = await settings.HandleAsync(new GetPlatformSettings(), cancellationToken);
        return platform.IsOn(Feature.StockLevels.Value)
            ? new StockPolicy(
                platform.StockDisplay switch
                {
                    StockDisplayModes.Levels => StockDisplayMode.Levels,
                    StockDisplayModes.Exact => StockDisplayMode.Exact,
                    var other => throw new InvalidOperationException($"Settings report an unknown stock display '{other}'."),
                },
                Amount.Parse(platform.LowStockThreshold).Match(threshold => threshold, error => throw new InvalidOperationException($"Settings report an invalid low-stock threshold: {error.Message}")))
            : Maybe<StockPolicy>.None;
    }

    public static string DisplayName(StockPolicy policy) =>
        policy.Mode switch
        {
            StockDisplayMode.Exact => StockDisplays.Exact,
            StockDisplayMode.Levels => StockDisplays.Levels,
            _ => throw new ArgumentOutOfRangeException(nameof(policy), policy.Mode, "A stock display without a name."),
        };

    public static string StatusName(StockStatus status) =>
        status switch
        {
            StockStatus.Available => StockStatuses.Available,
            StockStatus.Low => StockStatuses.Low,
            StockStatus.None => StockStatuses.None,
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "A stock status without a name."),
        };

    /// <summary>The presentation as its contract parts: the quantity and the delivery's quantity only in the exact display.</summary>
    public static (string Status, Maybe<decimal> Quantity, Maybe<DeliveryView> NextDelivery) View(StockPresentation shown) =>
        shown switch
        {
            StockPresentation.Exact exact => (StatusName(exact.Status), exact.Quantity.Value, exact.NextDelivery.Map(d => new DeliveryView(d.Date, d.Quantity.Map(q => q.Value).OrNull()))),
            StockPresentation.Levels levels => (StatusName(levels.Status), Maybe<decimal>.None, levels.NextDelivery.Map(date => new DeliveryView(date, null))),
            _ => throw new ArgumentOutOfRangeException(nameof(shown), shown, "A stock presentation without a view."),
        };
}

/// <summary>Stock in visible, active warehouses of the given SKUs as figures for today.</summary>
internal static class VisibleStock
{
    public static Result<IReadOnlyList<string>, Error> ValidateSkus(IReadOnlyList<string> skus, int max) =>
        skus.Count > max
            ? Error.Validation("stock.too-many-skus", $"At most {max} SKUs per query.")
            : skus.Traverse(sku => SourceCodes.Parse(sku.OrEmpty(), Stock.MaxSku, "sku", "SKU"));


    public static async Task<ILookup<string, StockFigure>> FiguresAsync(InventoryDbContext db, IReadOnlyList<string> skus, DateOnly today, CancellationToken cancellationToken)
    {
        var wanted = skus.Distinct().ToList();
        var rows = await (
                from stock in db.Stock.AsNoTracking()
                join warehouse in db.Warehouses on stock.WarehouseCode equals warehouse.Code
                where warehouse.Visible && warehouse.Active && wanted.Contains(stock.Sku)
                select stock)
            .ToListAsync(cancellationToken);
        return rows.ToLookup(r => r.Sku, r => Stock.Figure(r.Content(), today));
    }
}

/// <summary>Validate the SKUs → settings → stock in visible warehouses (one query) → pure sums and presentation.</summary>
internal sealed class GetStockSummariesHandler(InventoryDbContext db, InventoryClock clock, IQueryHandler<GetPlatformSettings, PlatformSettingsView> settings)
    : IQueryHandler<GetStockSummaries, Result<StockOverview, Error>>
{
    public Task<Result<StockOverview, Error>> HandleAsync(GetStockSummaries query, CancellationToken cancellationToken = default) =>
        VisibleStock.ValidateSkus(query.Skus.OrEmpty(), GetStockSummaries.MaxSkus).MapAsync(async skus =>
            await (await StockSettings.PolicyAsync(settings, cancellationToken)).Match(
                policy => OverviewAsync(skus, policy, cancellationToken),
                () => Task.FromResult(new StockOverview(StockDisplays.Hidden, []))));

    private async Task<StockOverview> OverviewAsync(IReadOnlyList<string> skus, StockPolicy policy, CancellationToken cancellationToken)
    {
        var bySku = await VisibleStock.FiguresAsync(db, skus, clock.Today, cancellationToken);

        return new StockOverview(
            StockSettings.DisplayName(policy),
            [.. skus.Select(sku =>
            {
                var (status, quantity, delivery) = StockSettings.View(Stock.Present(Stock.Total(bySku[sku]), policy));
                return new StockSummary(sku, status, quantity.OrNull(), delivery.OrNull());
            })]);
    }
}

/// <summary>Ordering availability: totals over visible warehouses, regardless of how stock is shown.</summary>
internal sealed class GetAvailabilityHandler(InventoryDbContext db, InventoryClock clock) : IQueryHandler<GetAvailability, Result<IReadOnlyList<Availability>, Error>>
{
    public Task<Result<IReadOnlyList<Availability>, Error>> HandleAsync(GetAvailability query, CancellationToken cancellationToken = default) =>
        VisibleStock.ValidateSkus(query.Skus.OrEmpty(), GetAvailability.MaxSkus).MapAsync(async skus =>
        {
            var bySku = await VisibleStock.FiguresAsync(db, skus, clock.Today, cancellationToken);
            return (IReadOnlyList<Availability>)[.. skus.Select(sku =>
            {
                var total = Stock.Total(bySku[sku]);
                return new Availability(sku, total.Quantity.Value, total.NextDelivery.Map(d => d.Date).OrNull());
            })];
        });
}

/// <summary>Stock of one product in every visible warehouse (also those without it) and in total.</summary>
internal sealed class GetProductStockHandler(InventoryDbContext db, InventoryClock clock, IQueryHandler<GetPlatformSettings, PlatformSettingsView> settings)
    : IQueryHandler<GetProductStock, Result<ProductStock, Error>>
{
    public Task<Result<ProductStock, Error>> HandleAsync(GetProductStock query, CancellationToken cancellationToken = default) =>
        SourceCodes.Parse(query.Sku.OrEmpty(), Stock.MaxSku, "sku", "SKU").MapAsync(async sku =>
            await (await StockSettings.PolicyAsync(settings, cancellationToken)).Match(
                policy => ProductAsync(sku, policy, cancellationToken),
                () => Task.FromResult(new ProductStock(StockDisplays.Hidden, null, []))));

    private async Task<ProductStock> ProductAsync(string sku, StockPolicy policy, CancellationToken cancellationToken)
    {
        var warehouses = await db.Warehouses.AsNoTracking()
            .Where(w => w.Visible && w.Active)
            .OrderBy(w => w.SortOrder).ThenBy(w => w.Name)
            .ToListAsync(cancellationToken);
        var codes = warehouses.Select(w => w.Code).ToList();
        var stock = await db.Stock.AsNoTracking()
            .Where(s => s.Sku == sku && codes.Contains(s.WarehouseCode))
            .ToDictionaryAsync(s => s.WarehouseCode, cancellationToken);
        var today = clock.Today;
        var figures = warehouses
            .Select(w => (Warehouse: w, Figure: stock.GetValueOrNone(w.Code).Match(row => Stock.Figure(row.Content(), today), () => StockFigure.Empty)))
            .ToList();

        var total = StockSettings.View(Stock.Present(Stock.Total(figures.Select(f => f.Figure)), policy));
        return new ProductStock(
            StockSettings.DisplayName(policy),
            new StockSummary(sku, total.Status, total.Quantity.OrNull(), total.NextDelivery.OrNull()),
            [.. figures.Select(f =>
            {
                var (status, quantity, delivery) = StockSettings.View(Stock.Present(f.Figure, policy));
                return new WarehouseStock(f.Warehouse.Code, f.Warehouse.Name, status, quantity.OrNull(), delivery.OrNull());
            })]);
    }
}
