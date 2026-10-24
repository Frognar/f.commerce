using System.Net;
using System.Net.Http.Json;
using FCommerce.Application.Messaging;
using FCommerce.Customers.Contracts;
using FCommerce.Pricing.Contracts;
using FCommerce.SharedKernel.Contracts;
using FCommerce.SharedKernel.Results;
using Microsoft.Extensions.DependencyInjection;

namespace FCommerce.IntegrationTests;

public sealed class PricingTests(HostFixture host)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string Unique(string prefix) => $"{prefix}-{Guid.CreateVersion7():N}"[..24];

    private async Task<(HttpStatusCode Status, ImportReport? Report)> PutAsync<T>(string path, T body, string? apiKey = HostFixture.IntegrationApiKey)
    {
        using var client = host.CreateClient();
        if (apiKey is not null)
        {
            client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        }

        using var response = await client.PutAsJsonAsync(path, body, Token);
        return (response.StatusCode, response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<ImportReport>(Token) : null);
    }

    private Task<(HttpStatusCode Status, ImportReport? Report)> ImportListsAsync(params PriceListData[] lists) =>
        PutAsync("/integration/v1/pricing/price-lists", new ImportPriceLists(lists));

    private Task<(HttpStatusCode Status, ImportReport? Report)> ImportPricesAsync(string list, params PriceData[] prices) =>
        PutAsync($"/integration/v1/pricing/price-lists/{Uri.EscapeDataString(list)}/prices", new PriceBatch(prices));

    private async Task<IReadOnlyList<ProductPrice>> PricesAsync(PriceViewer viewer, params PriceLine[] lines)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<IQueryHandler<GetPrices, Result<IReadOnlyList<ProductPrice>, Error>>>();
        return (await handler.HandleAsync(new GetPrices(viewer, lines), Token)).Match(prices => prices, error => throw new InvalidOperationException(error.Message));
    }

    private async Task<string> CustomerAsync(string? priceList, bool active = true)
    {
        var code = Unique("K");
        using var client = host.CreateIntegrationClient();
        var customer = new CustomerData(code, 1, $"Firma {code}", null, new AddressData("ul. Długa 1", "00-950", "Warszawa", "PL"), null, [], null,
            PriceListCode: priceList, Active: active);
        using var response = await client.PutAsJsonAsync("/integration/v1/customers", new ImportCustomers([customer]), Token);
        response.EnsureSuccessStatusCode();
        return code;
    }

    [Fact]
    public async Task Price_lists_and_prices_are_imported_with_reports()
    {
        var list = Unique("CL");
        var sku = Unique("SKU");

        var lists = await ImportListsAsync(new PriceListData(list, 1, "Cennik", "PLN"), new PriceListData(" ", 1, "Bez kodu", "PLN"));
        var prices = await ImportPricesAsync(
            list,
            new PriceData(sku, 1, 10m, 23m, [new PriceBreakData(50, 9.5m)]),
            new PriceData(Unique("SKU"), 1, 0m, 23m));
        var again = await ImportPricesAsync(list, new PriceData(sku, 2, 10m, 23m, [new PriceBreakData(50, 9.5m)]));
        var older = await ImportPricesAsync(list, new PriceData(sku, 1, 11m, 23m));

        Assert.Equal((HttpStatusCode.OK, 1, 1), (lists.Status, lists.Report!.Created, lists.Report.Rejected.Count));
        Assert.Equal((1, "net-price.not-positive", "netPrice"), (prices.Report!.Created, prices.Report.Rejected[0].Errors[0].Code, prices.Report.Rejected[0].Errors[0].Field));
        Assert.Equal(1, again.Report!.Unchanged);
        Assert.Equal(1, older.Report!.Stale);
    }

    [Fact]
    public async Task Prices_of_an_unknown_list_too_large_batches_and_missing_keys_are_refused()
    {
        var unknown = await ImportPricesAsync(Unique("NOPE"), new PriceData("X", 1, 1m, 23m));
        var tooLarge = await ImportListsAsync([.. Enumerable.Range(0, 1001).Select(i => new PriceListData($"L{i}", 1, "Cennik", "PLN"))]);
        var withoutKey = await PutAsync("/integration/v1/pricing/price-lists", new ImportPriceLists([]), apiKey: null);

        Assert.Equal(HttpStatusCode.NotFound, unknown.Status);
        Assert.Equal(HttpStatusCode.BadRequest, tooLarge.Status);
        Assert.Equal(HttpStatusCode.Unauthorized, withoutKey.Status);
    }

    [Fact]
    public async Task A_customer_sees_its_own_list_with_the_tier_for_the_quantity()
    {
        var list = Unique("HURT");
        var sku = Unique("SKU");
        await ImportListsAsync(new PriceListData(list, 1, "Hurt", "PLN"));
        await ImportPricesAsync(list, new PriceData(sku, 1, 10.01m, 23m, [new PriceBreakData(100, 9m)]));
        var customer = await CustomerAsync(list);

        var prices = await PricesAsync(new PriceViewer(true, customer), new PriceLine(sku, 1), new PriceLine(sku, 100), new PriceLine(Unique("SKU")));

        var unit = prices[0].Offer!;
        Assert.Equal((PriceStatuses.Priced, list, "PLN", 23m), (prices[0].Status, unit.PriceListCode, unit.Currency, unit.VatRate));
        Assert.Equal(new PriceTierView(1, 10.01m, 12.31m), unit.Unit);
        Assert.Equal([new PriceTierView(1, 10.01m, 12.31m), new PriceTierView(100, 9m, 11.07m)], unit.Tiers);
        Assert.Equal(new PriceTierView(100, 9m, 11.07m), prices[1].Offer!.Unit);
        Assert.Equal(PriceStatuses.OnRequest, prices[2].Status);
    }

    [Fact]
    public async Task Withdrawn_lists_inactive_customers_and_visitors_without_a_default_list_see_no_price()
    {
        var list = Unique("WYC");
        var sku = Unique("SKU");
        await ImportListsAsync(new PriceListData(list, 1, "Wycofany", "PLN"));
        await ImportPricesAsync(list, new PriceData(sku, 1, 5m, 23m));
        var inactive = await CustomerAsync(list, active: false);
        var active = await CustomerAsync(list);
        await ImportListsAsync(new PriceListData(list, 2, "Wycofany", "PLN", Active: false));

        var visitor = await PricesAsync(PriceViewer.Anonymous, new PriceLine(sku));
        var inactiveCustomer = await PricesAsync(new PriceViewer(true, inactive), new PriceLine(sku));
        var withdrawnList = await PricesAsync(new PriceViewer(true, active), new PriceLine(sku));

        Assert.Equal(PriceStatuses.SignInRequired, visitor[0].Status);
        Assert.Equal(PriceStatuses.OnRequest, inactiveCustomer[0].Status);
        Assert.Equal(PriceStatuses.OnRequest, withdrawnList[0].Status);
    }

    [Fact]
    public async Task The_back_office_offers_imported_lists_as_the_default_one()
    {
        var list = Unique("OFF");
        await ImportListsAsync(new PriceListData(list, 1, "Do wyboru", "EUR"));
        using var admin = await host.AdminAsync();

        var page = (await admin.GetAsync("/pl/admin/settings")).Html;

        Assert.Contains($"<option value=\"{list}\">{list} — Do wyboru (EUR)</option>", page, StringComparison.Ordinal);
    }
}
