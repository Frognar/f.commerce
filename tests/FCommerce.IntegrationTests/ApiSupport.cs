using System.Net.Http.Json;
using FCommerce.Access.Contracts;
using FCommerce.Application.Messaging;
using FCommerce.Catalog.Contracts;
using FCommerce.Customers.Contracts;
using FCommerce.Inventory.Contracts;
using FCommerce.Pricing.Contracts;
using FCommerce.SharedKernel.Results;
using Microsoft.Extensions.DependencyInjection;

namespace FCommerce.IntegrationTests;

/// <summary>A customer with an administrator, a buyer and an accountant (who may not order), one product priced 10 net with stock, and two delivery addresses.</summary>
public sealed record ApiCompany(string Code, string Sku, string AdminEmail, string BuyerEmail, Guid AdminId, Guid BuyerId, string PriceList, string AccountantEmail, Guid AccountantId);

/// <summary>Set-up shared by the tests of the public API: companies, keys and clients.</summary>
public sealed class ApiSupport(HostFixture host)
{
    public const string Password = "customer password 1";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public static string Unique(string prefix) => $"{prefix}-{Guid.CreateVersion7():N}"[..24];

    public static string UniqueEmail(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..20] + "@test.example.pl";

    public async Task PutAsync<T>(string path, T body)
    {
        using var client = host.CreateIntegrationClient();
        using var response = await client.PutAsJsonAsync(path, body, Token);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// A new company whose administrator and buyer accepted their invitations. The product costs 10 net (23% VAT) and
    /// 5 are in stock; the delivery addresses are MAG (default) and BUD.
    /// </summary>
    public async Task<ApiCompany> CompanyAsync(string? salesperson = null)
    {
        var sku = Unique("A");
        await PutAsync("/integration/v1/catalog/products", new ImportProducts([new ProductData(
            sku, 1, new Dictionary<string, string> { ["pl"] = $"Wkręt {sku}", ["en"] = $"Screw {sku}" }, null, null, "szt", null, [], [])]));
        var list = Unique("CL");
        await PutAsync("/integration/v1/pricing/price-lists", new ImportPriceLists([new PriceListData(list, 1, "Cennik", "PLN")]));
        await PutAsync($"/integration/v1/pricing/price-lists/{list}/prices", new PriceBatch([new PriceData(sku, 1, 10m, 23m)]));
        var warehouse = Unique("W");
        await PutAsync("/integration/v1/inventory/warehouses", new ImportWarehouses([new WarehouseData(warehouse, 1, "Magazyn")]));
        await PutAsync($"/integration/v1/inventory/warehouses/{warehouse}/stock", new StockBatch([new StockData(sku, 1, 5m, null, null)]));

        var code = Unique("K");
        var adminEmail = UniqueEmail("admin");
        var buyerEmail = UniqueEmail("buyer");
        var accountantEmail = UniqueEmail("accounting");
        var address = new AddressData("ul. Długa 1", "00-950", "Warszawa", "PL");
        await PutAsync("/integration/v1/customers", new ImportCustomers([new CustomerData(
            code, 1, $"Firma {code}", null, address,
            [new DeliveryAddressData("MAG", "Magazyn", address, IsDefault: true), new DeliveryAddressData("BUD", "Budowa", address with { Street = "ul. Krótka 2" })],
            [
                new ContactData("C1", "Anna Admin", adminEmail, null, PortalAccess: true, "administrator"),
                new ContactData("C2", "Piotr Kupiec", buyerEmail, null, PortalAccess: true, "buyer"),
                new ContactData("C3", "Ewa Księgowa", accountantEmail, null, PortalAccess: true, "accounting"),
            ],
            salesperson is null ? null : new SalespersonData("Anna Handlowiec", salesperson, null), PriceListCode: list)]));
        foreach (var email in new[] { adminEmail, buyerEmail, accountantEmail })
        {
            using var browser = host.NewBrowser();
            await host.AcceptInvitationAsync(browser, email, Password);
        }

        await using var scope = host.Factory.Services.CreateAsyncScope();
        var users = await scope.ServiceProvider.GetRequiredService<IQueryHandler<GetCustomerUsers, IReadOnlyList<CustomerUserSummary>>>()
            .HandleAsync(new GetCustomerUsers(code), Token);
        return new ApiCompany(code, sku, adminEmail, buyerEmail, users.Single(u => u.Email == adminEmail).UserId, users.Single(u => u.Email == buyerEmail).UserId, list, accountantEmail, users.Single(u => u.Email == accountantEmail).UserId);
    }

    /// <summary>Issues a key of <paramref name="owner"/> for the company, as its administrator would.</summary>
    public async Task<(string Key, Guid Id)> KeyAsync(ApiCompany company, Guid? owner = null, string name = "Test key", int? validForDays = null)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var created = await scope.ServiceProvider.GetRequiredService<ICommandHandler<CreateApiKey, Result<CreatedApiKey, Error>>>()
            .HandleAsync(new CreateApiKey(company.AdminId, owner ?? company.BuyerId, company.Code, name, validForDays), Token);
        return created.Match(c => (c.Key, c.Id), e => throw new InvalidOperationException(e.Code));
    }

    public async Task RevokeKeyAsync(ApiCompany company, Guid keyId)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<ICommandHandler<RevokeApiKey, Result<Unit, Error>>>()
            .HandleAsync(new RevokeApiKey(company.AdminId, company.Code, keyId), Token)).Match(_ => 0, e => throw new InvalidOperationException(e.Code));
    }

    public async Task<IReadOnlyList<ApiKeySummary>> KeysAsync(ApiCompany company)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IQueryHandler<GetCustomerApiKeys, IReadOnlyList<ApiKeySummary>>>()
            .HandleAsync(new GetCustomerApiKeys(company.Code), Token);
    }

    /// <summary>Repeats the call until its result satisfies the condition (search and read models catch up asynchronously).</summary>
    public static async Task<T> EventuallyAsync<T>(Func<Task<T>> call, Func<T, bool> condition, string because = "The read model did not catch up.")
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            var result = await call();
            if (condition(result))
            {
                return result;
            }

            Assert.True(DateTime.UtcNow < deadline, because);
            await Task.Delay(100, Token);
        }
    }

    /// <summary>A browser signed in as the user (who accepted the invitation already).</summary>
    public async Task<Browser> BrowserAsync(string email)
    {
        var browser = host.NewBrowser();
        await HostFixture.SignInAsync(browser, email, Password);
        return browser;
    }

    /// <summary>A client of the public API sending the key.</summary>
    public HttpClient ApiClient(string? key)
    {
        var client = host.CreateClient();
        if (key is not null)
        {
            client.DefaultRequestHeaders.Add("X-Api-Key", key);
        }

        return client;
    }
}
