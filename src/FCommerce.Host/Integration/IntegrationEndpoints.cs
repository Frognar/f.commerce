using FCommerce.SharedKernel.Imports;
using FCommerce.Application.Messaging;
using FCommerce.Catalog.Contracts;
using FCommerce.Customers.Contracts;
using FCommerce.Host.Http;
using FCommerce.SharedKernel.Results;

namespace FCommerce.Host.Integration;

/// <summary>API for the ERP connector: master data in (bulk upserts).</summary>
internal static class IntegrationEndpoints
{
    public const int MaxBatchSize = 1000;

    public static IEndpointRouteBuilder MapIntegrationApi(this IEndpointRouteBuilder app)
    {
        var integration = app.MapGroup("/integration/v1")
            .AddEndpointFilter<IntegrationApiKeyFilter>()
            .WithTags("Integration");

        integration.MapPut("/catalog/products", async (ImportProducts? command, ICommandHandler<ImportProducts, Result<ImportReport, Error>> handler, CancellationToken ct) =>
                await Batch(command?.Products, () => handler.HandleAsync(command!, ct)))
            .WithSummary("Upserts products; items are validated independently and reported.");

        integration.MapPut("/catalog/categories", async (ImportCategories? command, ICommandHandler<ImportCategories, Result<ImportReport, Error>> handler, CancellationToken ct) =>
                await Batch(command?.Categories, () => handler.HandleAsync(command!, ct)))
            .WithSummary("Upserts categories; a parent may arrive after its children.");

        integration.MapPut("/customers", async (ImportCustomers? command, ICommandHandler<ImportCustomers, Result<ImportReport, Error>> handler, CancellationToken ct) =>
                await Batch(command?.Customers, () => handler.HandleAsync(command!, ct)))
            .WithSummary("Upserts customers with their addresses, contacts and salesperson; items are validated independently and reported.");

        return app;
    }

    private static async Task<IResult> Batch<T>(IReadOnlyList<T>? items, Func<Task<Result<ImportReport, Error>>> import) =>
        items switch
        {
            null => Error.Validation("batch.missing", "The request body must contain the items to import.").ToProblem(),
            { Count: > MaxBatchSize } => Error.Validation("batch.too-large", $"At most {MaxBatchSize} items per request.").ToProblem(),
            _ => (await import()).ToHttpResult(Results.Ok),
        };
}
