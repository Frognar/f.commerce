using System.Text.Json;
using FCommerce.Access.Contracts;
using FCommerce.Access.Domain;
using FCommerce.Access.Persistence;
using FCommerce.Access.Security;
using FCommerce.Authorization.Core;
using FCommerce.Infrastructure.Outbox;
using FCommerce.SharedKernel.Contact;
using FCommerce.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace FCommerce.Access.Handlers;

/// <summary>Shared shell operations of the Access handlers (loading, auditing, issuing invitations).</summary>
internal sealed class AccessStore(AccessDbContext db, Outbox<AccessDbContext> outbox, TimeProvider time)
{
    public AccessDbContext Db => db;

    public DateTimeOffset Now => time.GetUtcNow();

    public async Task<AccountRow?> FindAsync(EmailAddress email, CancellationToken cancellationToken) =>
        await db.Accounts.SingleOrDefaultAsync(a => a.Email == email.Value, cancellationToken);

    public async Task<AccountRow?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Accounts.SingleOrDefaultAsync(a => a.Id == id, cancellationToken);

    /// <summary>The token row for a raw link token, if it exists.</summary>
    public async Task<TokenRow?> FindTokenAsync(string? token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 100)
        {
            return null;
        }

        var hash = Tokens.HashOf(token);
        return await db.Tokens.SingleOrDefaultAsync(t => t.Hash == hash, cancellationToken);
    }

    public void Audit(Guid? actor, string action, string subject, object? details = null) =>
        db.AuditLog.Add(new AuditEntry
        {
            At = Now,
            ActorId = actor,
            Action = action,
            Subject = subject,
            Details = details is null ? null : JsonSerializer.Serialize(details),
        });

    /// <summary>Issues an invitation link token for the account and queues the e-mail event.</summary>
    public Result<Unit, Error> Invite(Account account, string audience, string? customerName)
    {
        var (token, hash) = Tokens.New();
        db.Tokens.Add(TokenRow.From(OneTimeToken.Issue(account.Id, TokenPurpose.Invitation, hash, Now)));
        return outbox.Enqueue(new InvitationIssued(account.Id, account.Email.Value, account.Name.Value, account.Culture.Value, token, audience, customerName));
    }

    public Result<Unit, Error> Enqueue(SharedKernel.Messaging.IIntegrationEvent integrationEvent) => outbox.Enqueue(integrationEvent);

    /// <summary>The accounts' memberships, by account id.</summary>
    public async Task<ILookup<Guid, Membership>> MembershipsOfAsync(IReadOnlyCollection<Guid> accountIds, CancellationToken cancellationToken) =>
        (await db.Memberships.Where(m => accountIds.Contains(m.UserId)).ToListAsync(cancellationToken))
            .Select(m => m.ToDomain())
            .ToLookup(m => m.UserId);

    public static MembershipView ToView(Membership membership) =>
        new(
            membership.CustomerCode,
            membership.Attributes.CanOrder,
            ToView(membership.Attributes.OrderScope),
            membership.Attributes.OrderLimit?.Amount,
            membership.Attributes.OrderLimit?.Currency.Code,
            membership.Attributes.CanViewDocuments,
            membership.Attributes.CanManageUsers,
            membership.Attributes.DeliveryAddresses is DeliveryAddressScope.OnlyAddresses only ? [.. only.Codes] : null);

    public static MembershipRightsView ToView(MembershipRights rights) =>
        new(rights.CanOrder, ToView(rights.OrderScope), rights.CanViewDocuments, rights.CanManageUsers);

    public static Result<MembershipRights, Error> FromView(MembershipRightsView view) =>
        view.OrderScope switch
        {
            "own" => new MembershipRights(view.CanOrder, OrderScope.Own, view.CanViewDocuments, view.CanManageUsers),
            "company" => new MembershipRights(view.CanOrder, OrderScope.Company, view.CanViewDocuments, view.CanManageUsers),
            _ => Error.Validation("membership.order-scope-invalid", "Order scope must be 'own' or 'company'.") with { Field = "orderScope" },
        };

    private static string ToView(OrderScope scope) => scope == OrderScope.Company ? "company" : "own";

    public static Subject.Operator? AsOperator(Account account) =>
        account is { Status: AccountStatus.Active, Operator: { } attributes } ? new Subject.Operator(account.Id, account.Email, attributes) : null;

    public static OperatorView ToView(OperatorAttributes attributes) =>
        new(attributes.CanManageSettings, attributes.CanManageOperators, attributes.CanDecideRegistrations,
            attributes.CustomerScope == CustomerScope.All ? "all" : "as-salesperson");

    public static Result<OperatorAttributes, Error> FromView(OperatorView view) =>
        view.CustomerScope switch
        {
            "all" => new OperatorAttributes(view.CanManageSettings, view.CanManageOperators, view.CanDecideRegistrations, CustomerScope.All),
            "as-salesperson" => new OperatorAttributes(view.CanManageSettings, view.CanManageOperators, view.CanDecideRegistrations, CustomerScope.AsSalesperson),
            _ => Error.Validation("operator.scope-invalid", "Customer scope must be 'all' or 'as-salesperson'.") with { Field = "customerScope" },
        };
}
