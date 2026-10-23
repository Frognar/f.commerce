using FCommerce.Application.Messaging;
using FCommerce.Application.Results;
using FCommerce.Customers.Contracts;
using FCommerce.Customers.Domain;
using FCommerce.Customers.Persistence;
using FCommerce.Search.Core.Analysis;
using FCommerce.SharedKernel.Contact;
using FCommerce.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace FCommerce.Customers.Queries;

internal sealed class GetCustomerHandler(CustomersDbContext db) : IQueryHandler<GetCustomer, Result<CustomerDetails, Error>>
{
    public Task<Result<CustomerDetails, Error>> HandleAsync(GetCustomer query, CancellationToken cancellationToken = default) =>
        CustomerCode.Parse(query.Code).BindAsync(async code =>
            await db.Customers.AsNoTracking().SingleOrDefaultAsync(c => c.Code == code.Value, cancellationToken) is { } row
                ? Result.Ok<CustomerDetails, Error>(CustomerViews.Details(row.ToDomain(), row.UpdatedAt))
                : Error.NotFound("customer.not-found", $"Customer '{code}' does not exist."));
}

internal sealed class GetCustomersHandler(CustomersDbContext db) : IQueryHandler<GetCustomers, IReadOnlyList<CustomerSummary>>
{
    public async Task<IReadOnlyList<CustomerSummary>> HandleAsync(GetCustomers query, CancellationToken cancellationToken = default)
    {
        var codes = query.Codes.Distinct().ToList();
        return [.. (await db.Customers.AsNoTracking().Where(c => codes.Contains(c.Code)).OrderBy(c => c.Name).ThenBy(c => c.Code).ToListAsync(cancellationToken))
            .Select(CustomerViews.Summary)];
    }
}

internal sealed class SearchCustomersHandler(CustomersDbContext db) : IQueryHandler<SearchCustomers, Result<CustomerPage, Error>>
{
    public const int MaxPageSize = 100;

    public Task<Result<CustomerPage, Error>> HandleAsync(SearchCustomers query, CancellationToken cancellationToken = default) =>
        (string.IsNullOrWhiteSpace(query.SalespersonEmail)
            ? Result.Ok<EmailAddress?, Error>(null)
            : EmailAddress.Parse(query.SalespersonEmail).Map(email => (EmailAddress?)email))
        .MapAsync(salesperson => SearchAsync(query, salesperson, cancellationToken));

    private async Task<CustomerPage> SearchAsync(SearchCustomers query, EmailAddress? salesperson, CancellationToken cancellationToken)
    {
        var page = Math.Max(query.Page, 1);
        var pageSize = Math.Clamp(query.PageSize, 1, MaxPageSize);

        var customers = db.Customers.AsNoTracking();
        if (salesperson is not null)
        {
            customers = customers.Where(c => c.SalespersonEmail == salesperson.Value);
        }

        foreach (var term in Terms(query.Text))
        {
            var pattern = "%" + EscapeLike(term) + "%";
            customers = customers.Where(c => EF.Functions.Like(c.SearchText, pattern, "\\"));
        }

        var total = await customers.CountAsync(cancellationToken);
        var rows = await customers.OrderBy(c => c.Name).ThenBy(c => c.Code).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        return new CustomerPage([.. rows.Select(CustomerViews.Summary)], page, pageSize, total);
    }

    /// <summary>Folded words; a tax number written with separators ("526-104-08-28") becomes one term.</summary>
    private static IEnumerable<string> Terms(string? text) =>
        Folding.Fold(text ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(term => term.All(c => char.IsAsciiDigit(c) || c is '-') ? term.Replace("-", string.Empty, StringComparison.Ordinal) : term)
            .Where(term => term.Length > 0)
            .Take(8);

    private static string EscapeLike(string term) =>
        term.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
}

internal sealed class FindCustomersByTaxIdHandler(CustomersDbContext db) : IQueryHandler<FindCustomersByTaxId, Result<IReadOnlyList<CustomerSummary>, Error>>
{
    public Task<Result<IReadOnlyList<CustomerSummary>, Error>> HandleAsync(FindCustomersByTaxId query, CancellationToken cancellationToken = default) =>
        TaxId.Parse(query.TaxId).MapAsync(async taxId =>
            (IReadOnlyList<CustomerSummary>)[.. (await db.Customers.AsNoTracking().Where(c => c.TaxId == taxId.Value).OrderBy(c => c.Code).ToListAsync(cancellationToken))
                .Select(CustomerViews.Summary)]);
}

internal static class CustomerViews
{
    public static CustomerSummary Summary(CustomerRow row) =>
        Summary(row.ToDomain());

    public static CustomerSummary Summary(Customer customer) =>
        new(
            customer.Code.Value,
            customer.Content.Name,
            customer.Content.TaxId?.Value,
            customer.Content.BillingAddress.City,
            customer.Content.Salesperson?.Name,
            customer.Content.Salesperson?.Email?.Value,
            customer.Active,
            customer.Content.Terms.Blocked);

    public static CustomerDetails Details(Customer customer, DateTimeOffset updatedAt)
    {
        var content = customer.Content;
        return new CustomerDetails(
            customer.Code.Value,
            content.Name,
            content.TaxId?.Value,
            Address(content.BillingAddress),
            [.. content.DeliveryAddresses.Select(a => new DeliveryAddressView(a.Code, a.Name, Address(a.Address), a.IsDefault))],
            [.. content.Contacts.Select(c => new ContactView(c.ExternalId, c.Name, c.Email?.Value, c.Phone, c.PortalAccess, c.AttributeTemplate))],
            content.Salesperson is { } s ? new SalespersonView(s.Name, s.Email?.Value, s.Phone) : null,
            content.Terms.PriceListCode,
            content.Terms.PaymentTermDays,
            content.Terms.Currency.Code,
            content.Terms.Blocked,
            customer.Active,
            updatedAt);
    }

    private static AddressView Address(Address address) =>
        new(address.Street, address.PostalCode.Value, address.City, address.Country.Value);
}
