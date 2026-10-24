using System.Text.Json;
using FCommerce.Customers.Domain;
using FCommerce.Customers.Queries;
using FCommerce.Infrastructure.Serialization;
using FCommerce.Search.Core.Analysis;
using FCommerce.SharedKernel.Imports;
using FCommerce.SharedKernel.Optional;

namespace FCommerce.Customers.Persistence;

/// <summary>
/// Storage shape of a customer: columns for the back-office list and lookups, the validated content as JSON.
/// Mapping back to the domain trusts stored data (it was validated when written).
/// </summary>
internal sealed class CustomerRow
{
    public required string Code { get; init; }

    public long Version { get; set; }

    public required string Name { get; set; }

    public string? TaxId { get; set; }

    public required string City { get; set; }

    public string? SalespersonEmail { get; set; }

    public bool Active { get; set; }

    public bool Blocked { get; set; }

    public required string Content { get; set; }

    /// <summary>Folded code, name, tax id (with and without prefix) and cities, for substring search.</summary>
    public required string SearchText { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public static CustomerRow From(Customer customer, DateTimeOffset now)
    {
        var row = new CustomerRow { Code = customer.Code.Value, Name = string.Empty, City = string.Empty, Content = string.Empty, SearchText = string.Empty };
        row.Apply(customer, now);
        return row;
    }

    public void Apply(Customer customer, DateTimeOffset now)
    {
        var content = customer.Content;
        Version = customer.Version.Value;
        Name = content.Name;
        TaxId = content.TaxId.Map(t => t.Value).OrNull();
        City = content.BillingAddress.City;
        SalespersonEmail = CustomerViews.SalespersonEmail(content).OrNull();
        Active = customer.Active;
        Blocked = content.Terms.Blocked;
        Content = JsonSerializer.Serialize(content, PersistenceJson.Options);
        SearchText = SearchTextOf(customer);
        UpdatedAt = now;
    }

    public Customer ToDomain() =>
        new(
            Persisted.Rehydrate<CustomerCode, string>(Code),
            Persisted.Rehydrate<SourceVersion, long>(Version),
            JsonSerializer.Deserialize<CustomerContent>(Content, PersistenceJson.Options) ?? throw new JsonException("Stored customer content is null."),
            Active);

    private static string SearchTextOf(Customer customer) =>
        Folding.Fold(string.Join(
            ' ',
            new[] { customer.Code.Value, customer.Content.Name }
                .Concat(customer.Content.TaxId.AsEnumerable().SelectMany(t => new[] { t.Value, t.Number }))
                .Append(customer.Content.BillingAddress.City)
                .Concat(customer.Content.DeliveryAddresses.Select(a => a.Address.City))
                .Distinct()));
}
