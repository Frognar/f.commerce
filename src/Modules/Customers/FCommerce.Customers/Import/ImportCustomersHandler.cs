using FCommerce.Application.Messaging;
using FCommerce.Application.Results;
using FCommerce.Customers.Contracts;
using FCommerce.Customers.Domain;
using FCommerce.Customers.Persistence;
using FCommerce.Infrastructure.Outbox;
using FCommerce.SharedKernel.Imports;
using FCommerce.SharedKernel.Messaging;
using FCommerce.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace FCommerce.Customers.Import;

/// <summary>Validate every item (pure) → reconcile with stored customers (pure) → store changes and events in one transaction.</summary>
internal sealed class ImportCustomersHandler(CustomersDbContext db, Outbox<CustomersDbContext> outbox, TimeProvider time)
    : ICommandHandler<ImportCustomers, Result<ImportReport, Error>>
{
    public async Task<Result<ImportReport, Error>> HandleAsync(ImportCustomers command, CancellationToken cancellationToken = default)
    {
        var batch = ImportBatch.Validate(command.Customers, c => c.Code, c => Domain.Customers.Validate(ToUnvalidated(c)));

        var codes = batch.Valid.Select(v => v.Item.Code.Value).ToList();
        var rows = await db.Customers.Where(c => codes.Contains(c.Code)).ToDictionaryAsync(c => c.Code, cancellationToken);

        var changes = batch.Valid
            .Select(v => Domain.Customers.Reconcile(rows.GetValueOrDefault(v.Item.Code.Value)?.ToDomain(), v.Item))
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
                return ImportReport.From(command.Customers.Count, changes, batch.Rejected);
            });
    }

    private void Store(Change<Customer> change, Dictionary<string, CustomerRow> stored, DateTimeOffset now)
    {
        switch (change)
        {
            case Change<Customer>.Created created:
                db.Customers.Add(CustomerRow.From(created.Current, now));
                break;
            case Change<Customer>.Updated updated:
                stored[updated.Current.Code.Value].Apply(updated.Current, now);
                break;
            case Change<Customer>.Unchanged unchanged:
                stored[unchanged.Current.Code.Value].Version = unchanged.Current.Version.Value;
                break;
        }
    }

    private static IEnumerable<IIntegrationEvent> PublicEvents(Change<Customer> change) =>
        change switch
        {
            Change<Customer>.Created c => Events(null, c.Current),
            Change<Customer>.Updated u => Events(u.Previous, u.Current),
            _ => [],
        };

    private static IEnumerable<IIntegrationEvent> Events(Customer? before, Customer after)
    {
        yield return new CustomerChanged(after.Code.Value, after.Active, after.Content.Terms.Blocked, after.Content.Salesperson?.Email?.Value);
        if (Domain.Customers.PortalContactsChanged(before, after))
        {
            yield return new PortalContactsChanged(
                after.Code.Value,
                [.. after.Content.Contacts
                    .Where(c => c.PortalAccess && c.Email is not null)
                    .Select(c => new PortalContact(c.ExternalId, c.Name, c.Email!.Value, c.AttributeTemplate))]);
        }
    }

    private static UnvalidatedCustomer ToUnvalidated(CustomerData data) =>
        new(
            data.Code,
            data.Version,
            data.Name,
            data.TaxId,
            ToUnvalidated(data.BillingAddress),
            data.DeliveryAddresses?.Select(a => new UnvalidatedDeliveryAddress(a.Code, a.Name, ToUnvalidated(a.Address), a.IsDefault)).ToList(),
            data.Contacts?.Select(c => new UnvalidatedContact(c.ExternalId, c.Name, c.Email, c.Phone, c.PortalAccess, c.AttributeTemplate)).ToList(),
            data.Salesperson is { } s ? new UnvalidatedSalesperson(s.Name, s.Email, s.Phone) : null,
            data.PriceListCode,
            data.PaymentTermDays,
            data.Currency,
            data.Blocked,
            data.Active);

    private static UnvalidatedAddress? ToUnvalidated(AddressData? address) =>
        address is null ? null : new UnvalidatedAddress(address.Street, address.PostalCode, address.City, address.Country);
}
