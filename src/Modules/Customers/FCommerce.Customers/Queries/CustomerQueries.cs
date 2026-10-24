using FCommerce.Application.Messaging;
using FCommerce.Application.Results;
using FCommerce.Customers.Contracts;
using FCommerce.Customers.Domain;
using FCommerce.Customers.Persistence;
using FCommerce.Search.Core.Analysis;
using FCommerce.SharedKernel.Contact;
using FCommerce.SharedKernel.Optional;
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
        Maybe.ParseOptional(query.SalespersonEmail.ToMaybe(), EmailAddress.Parse)
            .MapAsync(salesperson => SearchAsync(query, salesperson, cancellationToken));

    private async Task<CustomerPage> SearchAsync(SearchCustomers query, Maybe<EmailAddress> salesperson, CancellationToken cancellationToken)
    {
        var page = Math.Max(query.Page, 1);
        var pageSize = Math.Clamp(query.PageSize, 1, MaxPageSize);

        var customers = salesperson.Match(
            email => db.Customers.AsNoTracking().Where(c => c.SalespersonEmail == email.Value),
            () => db.Customers.AsNoTracking());

        foreach (var term in Terms(query.Text.OrEmpty()))
        {
            var pattern = "%" + EscapeLike(term) + "%";
            customers = customers.Where(c => EF.Functions.Like(c.SearchText, pattern, "\\"));
        }

        var total = await customers.CountAsync(cancellationToken);
        var rows = await customers.OrderBy(c => c.Name).ThenBy(c => c.Code).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        return new CustomerPage([.. rows.Select(CustomerViews.Summary)], page, pageSize, total);
    }

    /// <summary>Folded words; a tax number written with separators ("526-104-08-28") becomes one term.</summary>
    private static IEnumerable<string> Terms(string text) =>
        Folding.Fold(text)
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
    public static Maybe<string> SalespersonEmail(CustomerContent content) =>
        content.Salesperson.Bind(s => s.Email).Map(email => email.Value);

    public static CustomerSummary Summary(CustomerRow row) =>
        Summary(row.ToDomain());

    public static CustomerSummary Summary(Customer customer) =>
        new(
            customer.Code.Value,
            customer.Content.Name,
            customer.Content.TaxId.Map(t => t.Value).OrNull(),
            customer.Content.BillingAddress.City,
            customer.Content.Salesperson.Map(s => s.Name).OrNull(),
            SalespersonEmail(customer.Content).OrNull(),
            customer.Active,
            customer.Content.Terms.Blocked);

    public static CustomerDetails Details(Customer customer, DateTimeOffset updatedAt)
    {
        var content = customer.Content;
        return new CustomerDetails(
            customer.Code.Value,
            content.Name,
            content.TaxId.Map(t => t.Value).OrNull(),
            Address(content.BillingAddress),
            [.. content.DeliveryAddresses.Select(a => new DeliveryAddressView(a.Code, a.Name.OrNull(), Address(a.Address), a.IsDefault))],
            [.. content.Contacts.Select(c => new ContactView(c.ExternalId, c.Name, c.Email.Map(e => e.Value).OrNull(), c.Phone.OrNull(), c.PortalAccess, c.AttributeTemplate.OrNull()))],
            content.Salesperson.Map(s => new SalespersonView(s.Name, s.Email.Map(e => e.Value).OrNull(), s.Phone.OrNull())).OrNull(),
            content.Terms.PriceListCode.OrNull(),
            content.Terms.PaymentTermDays.OrNull(),
            content.Terms.Currency.Code,
            content.Terms.Blocked,
            customer.Active,
            updatedAt);
    }

    private static AddressView Address(Address address) =>
        new(address.Street, address.PostalCode.Value, address.City, address.Country.Value);
}
