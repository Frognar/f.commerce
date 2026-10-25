using FCommerce.SharedKernel.Imports;
using FCommerce.Application.Messaging;
using FCommerce.Catalog.Contracts;
using FCommerce.Customers.Contracts;
using FCommerce.Documents.Contracts;
using FCommerce.Host.Http;
using FCommerce.Host.Security;
using FCommerce.Inventory.Contracts;
using FCommerce.Ordering.Contracts;
using FCommerce.Pricing.Contracts;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Results;
using Microsoft.AspNetCore.Mvc;
using FCommerce.SharedKernel.Contracts;

namespace FCommerce.Host.Integration;

/// <summary>API for the ERP connector: master data in (bulk upserts), orders out (a feed) and their confirmations in.</summary>
internal static class IntegrationEndpoints
{
    public const int MaxBatchSize = 1000;

    /// <summary>Slightly above the documents module's own limit, so it reports the size error instead of the server.</summary>
    private const long MaxDocumentFileRequestBytes = 21 * 1024 * 1024;

    public static IEndpointRouteBuilder MapIntegrationApi(this IEndpointRouteBuilder app)
    {
        var integration = app.MapGroup("/integration/v1")
            .AddEndpointFilter<IntegrationApiKeyFilter>()
            .WithTags("Integration");

        integration.MapPut("/catalog/products", (ImportProducts? command, ICommandHandler<ImportProducts, Result<ImportReport, Error>> handler, CancellationToken ct) =>
                Batch(command.ToMaybe(), c => c.Products, c => handler.HandleAsync(c, ct)))
            .WithSummary("Upserts products; items are validated independently and reported.");

        integration.MapPut("/catalog/categories", (ImportCategories? command, ICommandHandler<ImportCategories, Result<ImportReport, Error>> handler, CancellationToken ct) =>
                Batch(command.ToMaybe(), c => c.Categories, c => handler.HandleAsync(c, ct)))
            .WithSummary("Upserts categories; a parent may arrive after its children.");

        integration.MapPut("/customers", (ImportCustomers? command, ICommandHandler<ImportCustomers, Result<ImportReport, Error>> handler, CancellationToken ct) =>
                Batch(command.ToMaybe(), c => c.Customers, c => handler.HandleAsync(c, ct)))
            .WithSummary("Upserts customers with their addresses, contacts and salesperson; items are validated independently and reported.");

        integration.MapPut("/documents", (ImportDocuments? command, ICommandHandler<ImportDocuments, Result<ImportReport, Error>> handler, CancellationToken ct) =>
                Batch(command.ToMaybe(), c => c.Documents, c => handler.HandleAsync(c, ct)))
            .WithSummary("Upserts document metadata (invoices, corrections, delivery notes, proformas); items are validated independently and reported.");

        integration.MapPut("/pricing/price-lists", (ImportPriceLists? command, ICommandHandler<ImportPriceLists, Result<ImportReport, Error>> handler, CancellationToken ct) =>
                Batch(command.ToMaybe(), c => c.PriceLists, c => handler.HandleAsync(c, ct)))
            .WithSummary("Upserts price lists (code, name, currency); their prices are imported per price list.");

        integration.MapPut("/pricing/price-lists/{code}/prices", (string code, PriceBatch? body, ICommandHandler<ImportPrices, Result<ImportReport, Error>> handler, CancellationToken ct) =>
                Batch(body.ToMaybe(), b => b.Prices, b => handler.HandleAsync(new ImportPrices(Uri.UnescapeDataString(code), b.Prices), ct)))
            .WithSummary("Upserts prices of an imported price list (net price, VAT rate, quantity breaks), keyed by SKU.");

        integration.MapPut("/inventory/warehouses", (ImportWarehouses? command, ICommandHandler<ImportWarehouses, Result<ImportReport, Error>> handler, CancellationToken ct) =>
                Batch(command.ToMaybe(), c => c.Warehouses, c => handler.HandleAsync(c, ct)))
            .WithSummary("Upserts warehouses (code, name, visibility for buyers, order); their stock is imported per warehouse.");

        integration.MapPut("/inventory/warehouses/{code}/stock", (string code, StockBatch? body, ICommandHandler<ImportStock, Result<ImportReport, Error>> handler, CancellationToken ct) =>
                Batch(body.ToMaybe(), b => b.Stock, b => handler.HandleAsync(new ImportStock(Uri.UnescapeDataString(code), b.Stock), ct)))
            .WithSummary("Upserts stock of an imported warehouse (quantity, next delivery), keyed by SKU; send zero instead of deleting.");

        integration.MapPut("/documents/{id}/file", StoreDocumentFileAsync)
            .WithMetadata(new RequestSizeLimitAttribute(MaxDocumentFileRequestBytes))
            .Accepts<byte[]>("application/pdf")
            .WithSummary("Stores the PDF (raw body) of an imported document; an id with \"/\" is sent URL-encoded.");

        var orders = integration.MapGroup("/orders").RequireFeature("order-feed");

        orders.MapGet("/feed", async (long? after, int? limit, IQueryHandler<GetOrderFeed, OrderFeedPage> handler, CancellationToken ct) =>
                Results.Ok(await handler.HandleAsync(new GetOrderFeed(Math.Max(after ?? 0, 0), limit ?? 100), ct)))
            .WithSummary("Placed orders and orders the buyer cancelled, after a position (0: from the start); keep lastPosition and ask after it next time.");

        orders.MapPut("/{number}/acceptance", async (string number, OrderAcceptanceData? body, ICommandHandler<AcceptOrder, Result<Unit, Error>> handler, CancellationToken ct) =>
                (await handler.HandleAsync(new AcceptOrder(Uri.UnescapeDataString(number), body?.ErpNumber ?? string.Empty), ct)).ToHttpResult(_ => Results.NoContent()))
            .WithSummary("The ERP took the order over under its own number (\"/\" in the platform number sent URL-encoded); repeating it changes nothing.");

        orders.MapPut("/{number}/rejection", async (string number, OrderReasonData? body, ICommandHandler<RejectOrder, Result<Unit, Error>> handler, CancellationToken ct) =>
                (await handler.HandleAsync(new RejectOrder(Uri.UnescapeDataString(number), body?.Reason), ct)).ToHttpResult(_ => Results.NoContent()))
            .WithSummary("The ERP refuses an order it has not accepted; 409 for an accepted or cancelled order.");

        orders.MapPut("/{number}/cancellation", async (string number, OrderReasonData? body, ICommandHandler<CancelOrderBySeller, Result<Unit, Error>> handler, CancellationToken ct) =>
                (await handler.HandleAsync(new CancelOrderBySeller(Uri.UnescapeDataString(number), body?.Reason), ct)).ToHttpResult(_ => Results.NoContent()))
            .WithSummary("The seller cancels an order that is not invoiced yet.");

        return app;
    }

    private static async Task<IResult> StoreDocumentFileAsync(string id, HttpRequest request, ICommandHandler<StoreDocumentFile, Result<Unit, Error>> handler, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, ct);
        // Routing leaves an encoded "/" (%2F) in the value.
        var documentId = Uri.UnescapeDataString(id);
        return (await handler.HandleAsync(new StoreDocumentFile(documentId, buffer.GetBuffer().AsMemory(0, (int)buffer.Length)), ct)).ToHttpResult(_ => Results.NoContent());
    }

    /// <param name="command">The body; missing when the request had none.</param>
    /// <param name="items">The batch items of the body (the JSON may lack them).</param>
    private static async Task<IResult> Batch<TCommand, T>(Maybe<TCommand> command, Func<TCommand, IReadOnlyList<T>> items, Func<TCommand, Task<Result<ImportReport, Error>>> import)
        where TCommand : notnull
        where T : notnull =>
        await command.Bind(c => items(c).ToMaybe().Map(list => (Command: c, list.Count))).Match(
            async batch => batch.Count > MaxBatchSize
                ? Error.Validation("batch.too-large", $"At most {MaxBatchSize} items per request.").ToProblem()
                : (await import(batch.Command)).ToHttpResult(Results.Ok),
            () => Task.FromResult(Error.Validation("batch.missing", "The request body must contain the items to import.").ToProblem()));
}
