using FCommerce.Application.Messaging;
using FCommerce.Application.Results;
using FCommerce.Customers.Contracts;
using FCommerce.Customers.Domain;
using FCommerce.Customers.Persistence;
using FCommerce.Customers.Queries;
using FCommerce.Infrastructure.Outbox;
using FCommerce.SharedKernel.Imports;
using FCommerce.SharedKernel.Messaging;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using FCommerce.SharedKernel.Contracts;

namespace FCommerce.Customers.Import;

/// <summary>Validate every item (pure) → reconcile with stored customers (pure) → store changes and events in one transaction.</summary>
internal sealed class ImportCustomersHandler(CustomersDbContext db, Outbox<CustomersDbContext> outbox, TimeProvider time)
    : ICommandHandler<ImportCustomers, Result<ImportReport, Error>>
{
    public async Task<Result<ImportReport, Error>> HandleAsync(ImportCustomers command, CancellationToken cancellationToken = default)
    {
        var batch = ImportBatch.Validate(command.Customers, c => c.Code.ToMaybe(), c => Domain.Customers.Validate(ToUnvalidated(c)));

        var codes = batch.Valid.Select(v => v.Item.Code.Value).ToList();
        var rows = await db.Customers.Where(c => codes.Contains(c.Code)).ToDictionaryAsync(c => c.Code, cancellationToken);

        var changes = batch.Valid
            .Select(v => Domain.Customers.Reconcile(rows.GetValueOrNone(v.Item.Code.Value).Map(row => row.ToDomain()), v.Item))
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
            Change<Customer>.Created c => Events(Maybe<Customer>.None, c.Current),
            Change<Customer>.Updated u => Events(u.Previous, u.Current),
            _ => [],
        };

    private static IEnumerable<IIntegrationEvent> Events(Maybe<Customer> before, Customer after)
    {
        yield return new CustomerChanged(after.Code.Value, after.Active, after.Content.Terms.Blocked, CustomerViews.SalespersonEmail(after.Content).OrNull());
        if (Domain.Customers.PortalContactsChanged(before, after))
        {
            yield return new PortalContactsChanged(
                after.Code.Value,
                [.. after.Content.Contacts
                    .Where(c => c.PortalAccess)
                    .Choose(c => c.Email.Map(email => new PortalContact(c.ExternalId, c.Name, email.Value, c.AttributeTemplate.OrNull())))]);
        }
    }

    private static UnvalidatedCustomer ToUnvalidated(CustomerData data) =>
        new(
            data.Code.OrEmpty(),
            data.Version,
            data.Name.OrEmpty(),
            data.TaxId.ToMaybe(),
            ToUnvalidated(data.BillingAddress.ToMaybe()),
            [.. data.DeliveryAddresses.OrEmpty().Select(a => new UnvalidatedDeliveryAddress(a.Code.OrEmpty(), a.Name.ToMaybe(), ToUnvalidated(a.Address.ToMaybe()), a.IsDefault))],
            [.. data.Contacts.OrEmpty().Select(c => new UnvalidatedContact(c.ExternalId.OrEmpty(), c.Name.OrEmpty(), c.Email.ToMaybe(), c.Phone.ToMaybe(), c.PortalAccess, c.AttributeTemplate.ToMaybe()))],
            data.Salesperson.ToMaybe().Map(s => new UnvalidatedSalesperson(s.Name.OrEmpty(), s.Email.ToMaybe(), s.Phone.ToMaybe())),
            data.PriceListCode.ToMaybe(),
            data.PaymentTermDays.ToMaybe(),
            data.Currency.ToMaybe(),
            data.Blocked,
            data.Active);

    private static Maybe<UnvalidatedAddress> ToUnvalidated(Maybe<AddressData> address) =>
        address.Map(a => new UnvalidatedAddress(a.Street.OrEmpty(), a.PostalCode.OrEmpty(), a.City.OrEmpty(), a.Country.OrEmpty()));
}
