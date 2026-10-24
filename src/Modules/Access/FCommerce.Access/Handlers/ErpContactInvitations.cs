using FCommerce.Access.Domain;
using FCommerce.Access.Persistence;
using FCommerce.Access.Security;
using FCommerce.Application.Messaging;
using FCommerce.Customers.Contracts;
using FCommerce.Settings.Contracts;
using FCommerce.SharedKernel.Contact;
using FCommerce.SharedKernel.Features;
using FCommerce.SharedKernel.Localization;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FCommerce.Access.Handlers;

/// <summary>
/// ERP contacts with portal access become customer users (feature "erp-contact-invitations"). Planning is pure
/// (<see cref="ErpContactSync"/>) and runs against the current state, so a redelivered event changes nothing.
/// </summary>
internal sealed partial class ErpContactInvitations(
    AccessStore store,
    MembershipManagement memberships,
    AccessOptions options,
    IQueryHandler<GetCustomer, Result<CustomerDetails, Error>> customers,
    IQueryHandler<GetPlatformSettings, PlatformSettingsView> settings,
    ILogger<ErpContactInvitations> logger)
    : IIntegrationEventHandler<PortalContactsChanged>
{
    public async Task HandleAsync(PortalContactsChanged changed, CancellationToken cancellationToken = default)
    {
        var current = await settings.HandleAsync(new GetPlatformSettings(), cancellationToken);
        if (!current.IsOn(Feature.ErpContactInvitations.Value))
        {
            return;
        }

        await (await customers.HandleAsync(new GetCustomer(changed.CustomerCode), cancellationToken)).ToMaybe().Match(
            customer => SyncAsync(customer, changed, current, cancellationToken),
            () =>
            {
                LogUnknownCustomer(changed.CustomerCode);
                return Task.CompletedTask;
            });
    }

    private async Task SyncAsync(CustomerDetails customer, PortalContactsChanged changed, PlatformSettingsView current, CancellationToken cancellationToken)
    {
        var contacts = changed.Contacts
            .Choose(c =>
                (from email in EmailAddress.Parse(c.Email.OrEmpty())
                 from name in PersonName.Parse(c.Name.OrEmpty())
                 select new ErpContactSync.Contact(c.ExternalId, email, name, MembershipRules.FromTemplate(c.AttributeTemplate.ToMaybe().Or(current.DefaultContactTemplate))))
                .ToMaybe())
            .ToList();

        var emails = contacts.Select(c => c.Email.Value).ToList();
        var memberIds = await store.Db.Memberships.Where(m => m.CustomerCode == customer.Code).Select(m => m.UserId).ToListAsync(cancellationToken);
        var accounts = (await store.Db.Accounts.Where(a => emails.Contains(a.Email) || memberIds.Contains(a.Id)).ToListAsync(cancellationToken))
            .Select(a => a.ToDomain())
            .ToList();
        var accountMemberships = await store.MembershipsOfAsync([.. accounts.Select(a => a.Id)], cancellationToken);
        var existing = accounts.Select(a => new ErpContactSync.Member(a, [.. accountMemberships[a.Id]])).ToList();

        var steps = ErpContactSync.Plan(customer.Code, contacts, existing, current.IsOn(Feature.MultiCustomerAccounts.Value));
        var applied = steps.Select(step => Apply(step, customer)).ToList();

        await applied.Sequence().Match(
            async _ => await store.Db.SaveChangesAsync(cancellationToken),
            error => throw new InvalidOperationException($"ERP contacts of {customer.Code}: {error.Message}"));
    }

    private Result<Unit, Error> Apply(ErpContactSync.Step step, CustomerDetails customer)
    {
        var subject = MembershipManagement.Subject(customer.Code);
        switch (step)
        {
            case ErpContactSync.Step.CreateAccount create:
                return CultureCode.Parse(options.DefaultCulture).Bind(culture =>
                {
                    var account = new Account(
                        Guid.CreateVersion7(), create.Contact.Email, create.Contact.Name, culture, AccountStatus.Invited, Maybe<string>.None,
                        Tokens.NewSecurityStamp(), 0, Maybe<DateTimeOffset>.None, Maybe<Authorization.Core.OperatorAttributes>.None);
                    var membership = ErpContactSync.ErpMembership(account.Id, customer.Code, create.Contact);
                    store.Db.Accounts.Add(AccountRow.From(account, store.Now));
                    store.Audit(AccessStore.SystemActor, "membership.granted", subject, MembershipManagement.Details(account.Email, membership));
                    return memberships.Grant(account, membership, customer.Name);
                });

            case ErpContactSync.Step.Grant grant:
                store.Audit(AccessStore.SystemActor, "membership.granted", subject, MembershipManagement.Details(grant.Account.Email, grant.Membership));
                return memberships.Grant(grant.Account, grant.Membership, customer.Name);

            case ErpContactSync.Step.Revoke revoke:
                store.Db.Memberships.Remove(store.Db.Memberships.Local.Single(m => m.UserId == revoke.Membership.UserId && m.CustomerCode == customer.Code));
                store.Audit(AccessStore.SystemActor, "membership.revoked", subject, MembershipManagement.Details(revoke.Email, revoke.Membership));
                return Unit.Value;

            case ErpContactSync.Step.Skip skip:
                store.Audit(AccessStore.SystemActor, "membership.erp-skipped", subject, new { email = skip.Contact.Email.Value, reason = skip.Reason.Code });
                return Unit.Value;

            default:
                return Error.Conflict("erp-sync.unknown-step", $"Unknown step {step.GetType().Name}.");
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Portal contacts of unknown customer {Code} ignored")]
    private partial void LogUnknownCustomer(string code);
}
