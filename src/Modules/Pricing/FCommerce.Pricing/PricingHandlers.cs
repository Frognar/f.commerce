using FCommerce.Application.Messaging;
using FCommerce.Application.Results;
using FCommerce.Customers.Contracts;
using FCommerce.Pricing.Contracts;
using FCommerce.Pricing.Domain;
using FCommerce.Pricing.Persistence;
using FCommerce.Settings.Contracts;
using FCommerce.SharedKernel.Contracts;
using FCommerce.SharedKernel.Features;
using FCommerce.SharedKernel.Imports;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace FCommerce.Pricing;

/// <summary>Validate every item (pure) → reconcile with stored price lists (pure) → store the changes.</summary>
internal sealed class ImportPriceListsHandler(PricingDbContext db, TimeProvider time) : ICommandHandler<ImportPriceLists, Result<ImportReport, Error>>
{
    public async Task<Result<ImportReport, Error>> HandleAsync(ImportPriceLists command, CancellationToken cancellationToken = default)
    {
        var batch = ImportBatch.Validate(
            command.PriceLists,
            l => l.Code.ToMaybe(),
            l => Prices.Validate(new UnvalidatedPriceList(l.Code.OrEmpty(), l.Version, l.Name.OrEmpty(), l.Currency.OrEmpty(), l.Active)));

        var codes = batch.Valid.Select(v => v.Item.Code.Value).ToList();
        var rows = await db.PriceLists.Where(l => codes.Contains(l.Code)).ToDictionaryAsync(l => l.Code, cancellationToken);
        var changes = batch.Valid
            .Select(v => Prices.Reconcile(rows.GetValueOrNone(v.Item.Code.Value).Map(row => row.ToDomain()), v.Item))
            .ToList();

        var now = time.GetUtcNow();
        foreach (var change in changes)
        {
            switch (change)
            {
                case Change<PriceList>.Created created:
                    db.PriceLists.Add(PriceListRow.From(created.Current, now));
                    break;
                case Change<PriceList>.Updated updated:
                    rows[updated.Current.Code.Value].Apply(updated.Current, now);
                    break;
                case Change<PriceList>.Unchanged unchanged:
                    rows[unchanged.Current.Code.Value].Version = unchanged.Current.Version.Value;
                    break;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return ImportReport.From(command.PriceLists.Count, changes, batch.Rejected);
    }
}

/// <summary>Prices of one imported price list, keyed by SKU; the same pattern as the price lists.</summary>
internal sealed class ImportPricesHandler(PricingDbContext db, TimeProvider time) : ICommandHandler<ImportPrices, Result<ImportReport, Error>>
{
    public Task<Result<ImportReport, Error>> HandleAsync(ImportPrices command, CancellationToken cancellationToken = default) =>
        PriceListCode.Parse(command.PriceListCode.OrEmpty()).BindAsync(async code =>
            await db.PriceLists.AnyAsync(l => l.Code == code.Value, cancellationToken)
                ? Result.Ok<ImportReport, Error>(await ImportAsync(code, command.Prices, cancellationToken))
                : Error.NotFound("price-list.not-found", $"Price list '{code}' has not been imported."));

    private async Task<ImportReport> ImportAsync(PriceListCode code, IReadOnlyList<PriceData> prices, CancellationToken cancellationToken)
    {
        var batch = ImportBatch.Validate(prices, p => p.Sku.ToMaybe(), p => Prices.Validate(ToUnvalidated(p)));

        var skus = batch.Valid.Select(v => v.Item.Sku).ToList();
        var rows = await db.Prices.Where(p => p.PriceListCode == code.Value && skus.Contains(p.Sku)).ToDictionaryAsync(p => p.Sku, cancellationToken);
        var changes = batch.Valid
            .Select(v => Prices.Reconcile(rows.GetValueOrNone(v.Item.Sku).Map(row => row.ToDomain()), v.Item))
            .ToList();

        var now = time.GetUtcNow();
        foreach (var change in changes)
        {
            switch (change)
            {
                case Change<Price>.Created created:
                    db.Prices.Add(PriceRow.From(code, created.Current, now));
                    break;
                case Change<Price>.Updated updated:
                    rows[updated.Current.Sku].Apply(updated.Current, now);
                    break;
                case Change<Price>.Unchanged unchanged:
                    rows[unchanged.Current.Sku].Version = unchanged.Current.Version.Value;
                    break;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return ImportReport.From(prices.Count, changes, batch.Rejected);
    }

    private static UnvalidatedPrice ToUnvalidated(PriceData data) =>
        new(
            data.Sku.OrEmpty(),
            data.Version,
            data.NetPrice,
            data.VatRate,
            [.. data.Breaks.OrEmpty().Select(b => new PriceBreak(b.FromQuantity, b.NetPrice))],
            data.Active);
}

/// <summary>
/// Validate the lines → settings and the viewer's customer (through their contracts) → the active price lists
/// involved (at most two rows) → their prices of the requested SKUs (one query) → pure resolution.
/// </summary>
internal sealed class GetPricesHandler(
    PricingDbContext db,
    IQueryHandler<GetPlatformSettings, PlatformSettingsView> settings,
    IQueryHandler<GetCustomer, Result<CustomerDetails, Error>> customers)
    : IQueryHandler<GetPrices, Result<IReadOnlyList<ProductPrice>, Error>>
{
    public Task<Result<IReadOnlyList<ProductPrice>, Error>> HandleAsync(GetPrices query, CancellationToken cancellationToken = default) =>
        ValidateLines(query.Lines.OrEmpty()).BindAsync(async lines =>
        {
            var platform = await settings.HandleAsync(new GetPlatformSettings(), cancellationToken);
            var customer = await CustomerAsync(query.Viewer, cancellationToken);
            var defaultCode = platform.DefaultPriceListCode.ToMaybe().Bind(code => PriceListCode.Parse(code).ToMaybe());

            var wanted = defaultCode.AsEnumerable().Concat(customer.Bind(c => c.PriceList).AsEnumerable()).Select(c => c.Value).ToList();
            var lists = await db.PriceLists.AsNoTracking()
                .Where(l => wanted.Contains(l.Code) && l.Active)
                .ToDictionaryAsync(l => l.Code, l => l.ToActive(), cancellationToken);
            Maybe<ActivePriceList> ActiveList(PriceListCode code) => lists.GetValueOrNone(code.Value);

            var viewer = PriceResolution.Viewer(query.Viewer.SignedIn, customer, ActiveList);
            var sources = PriceResolution.Sources(viewer, new PricingPolicy(defaultCode.Bind(ActiveList), platform.IsOn(Feature.PublicPrices.Value)));
            var prices = await PricesAsync(sources.Select(s => s.Code.Value).ToList(), lines.Select(l => l.Sku).Distinct().ToList(), cancellationToken);

            return Result.Ok<IReadOnlyList<ProductPrice>, Error>(
                [.. lines.Select(line => View(line.Sku, line.Quantity, PriceResolution.Resolve(viewer, sources, code => prices.GetValueOrNone((code.Value, line.Sku)))))]);
        });

    private static Result<IReadOnlyList<(string Sku, decimal Quantity)>, Error> ValidateLines(IReadOnlyList<PriceLine> lines) =>
        lines.Count > GetPrices.MaxLines
            ? Error.Validation("prices.too-many-lines", $"At most {GetPrices.MaxLines} lines per query.")
            : lines.Traverse(line =>
                from sku in SourceCodes.Parse(line.Sku.OrEmpty(), Prices.MaxSku, "sku", "SKU")
                from quantity in Prices.ParseQuantity(line.Quantity)
                select (sku, quantity));

    /// <summary>The viewer's customer as pricing sees it; none without a customer or when it does not exist.</summary>
    private async Task<Maybe<CustomerTerms>> CustomerAsync(PriceViewer viewer, CancellationToken cancellationToken) =>
        await viewer.CustomerCode.ToMaybe().Where(_ => viewer.SignedIn).Match(
            async code => (await customers.HandleAsync(new GetCustomer(code), cancellationToken)).ToMaybe()
                .Map(c => new CustomerTerms(c.Active, c.PriceListCode.ToMaybe().Bind(list => PriceListCode.Parse(list).ToMaybe()))),
            () => Task.FromResult(Maybe<CustomerTerms>.None));

    private async Task<Dictionary<(string List, string Sku), PriceContent>> PricesAsync(List<string> lists, List<string> skus, CancellationToken cancellationToken) =>
        lists.Count == 0
            ? []
            : (await db.Prices.AsNoTracking()
                .Where(p => lists.Contains(p.PriceListCode) && skus.Contains(p.Sku) && p.Active)
                .ToListAsync(cancellationToken))
                .ToDictionary(p => (p.PriceListCode, p.Sku), p => p.Content());

    private static ProductPrice View(string sku, decimal quantity, PriceOutcome outcome) =>
        outcome switch
        {
            PriceOutcome.Priced priced => new ProductPrice(sku, quantity, PriceStatuses.Priced, Offer(priced, quantity)),
            PriceOutcome.SignInRequired => new ProductPrice(sku, quantity, PriceStatuses.SignInRequired, null),
            _ => new ProductPrice(sku, quantity, PriceStatuses.OnRequest, null),
        };

    private static OfferedPrice Offer(PriceOutcome.Priced priced, decimal quantity)
    {
        var vat = priced.Price.Vat;
        PriceTierView Tier(PriceBreak tier) => new(tier.FromQuantity, tier.Net, Prices.GrossUnit(tier.Net, vat));
        return new OfferedPrice(
            priced.List.Code.Value,
            priced.List.Currency.Code,
            vat.Percent,
            Tier(priced.Price.Tiers.For(quantity)),
            [.. priced.Price.Tiers.All.Select(Tier)]);
    }
}

internal sealed class GetPriceListsHandler(PricingDbContext db) : IQueryHandler<GetPriceLists, IReadOnlyList<PriceListSummary>>
{
    public async Task<IReadOnlyList<PriceListSummary>> HandleAsync(GetPriceLists query, CancellationToken cancellationToken = default) =>
        [.. (await db.PriceLists.AsNoTracking().OrderBy(l => l.Code).ToListAsync(cancellationToken))
            .Select(l => new PriceListSummary(l.Code, l.Name, l.Currency, l.Active))];
}
