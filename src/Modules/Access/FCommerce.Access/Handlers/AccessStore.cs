using System.Text.Json;
using FCommerce.Access.Contracts;
using FCommerce.Access.Domain;
using FCommerce.Access.Persistence;
using FCommerce.Access.Security;
using FCommerce.Authorization.Core;
using FCommerce.Infrastructure.Outbox;
using FCommerce.Infrastructure.Persistence;
using FCommerce.SharedKernel.Contact;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace FCommerce.Access.Handlers;

/// <summary>Shared shell operations of the Access handlers (loading, auditing, issuing invitations).</summary>
internal sealed class AccessStore(AccessDbContext db, Outbox<AccessDbContext> outbox, TimeProvider time)
{
    public AccessDbContext Db => db;

    public DateTimeOffset Now => time.GetUtcNow();

    /// <summary>The actor of changes the platform makes itself (bootstrap, ERP-driven changes, public forms).</summary>
    public static Maybe<Guid> SystemActor => Maybe<Guid>.None;

    public Task<Maybe<AccountRow>> FindAsync(EmailAddress email, CancellationToken cancellationToken) =>
        db.Accounts.SingleOrNoneAsync(a => a.Email == email.Value, cancellationToken);

    public Task<Maybe<AccountRow>> FindAsync(Guid id, CancellationToken cancellationToken) =>
        db.Accounts.SingleOrNoneAsync(a => a.Id == id, cancellationToken);

    /// <summary>The token row for a raw link token, if it exists.</summary>
    public async Task<Maybe<TokenRow>> FindTokenAsync(string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 100)
        {
            return Maybe<TokenRow>.None;
        }

        var hash = Tokens.HashOf(token);
        return await db.Tokens.SingleOrNoneAsync(t => t.Hash == hash, cancellationToken);
    }

    /// <summary>The account a link token was issued for, with the token.</summary>
    public async Task<Maybe<(TokenRow Token, AccountRow Account)>> FindTokenAccountAsync(string token, CancellationToken cancellationToken) =>
        await (await FindTokenAsync(token, cancellationToken)).Match(
            async row => (await FindAsync(row.AccountId, cancellationToken)).Map(account => (row, account)),
            () => Task.FromResult(Maybe<(TokenRow, AccountRow)>.None));

    public void Audit(Maybe<Guid> actor, string action, string subject) =>
        Audit(actor, action, subject, Maybe<string>.None);

    public void Audit(Maybe<Guid> actor, string action, string subject, object details) =>
        Audit(actor, action, subject, JsonSerializer.Serialize(details));

    /// <summary>Issues an invitation link token for the account and queues the e-mail event.</summary>
    public Result<Unit, Error> Invite(Account account, string audience, Maybe<string> customerName)
    {
        var (token, hash) = Tokens.New();
        db.Tokens.Add(TokenRow.From(OneTimeToken.Issue(account.Id, TokenPurpose.Invitation, hash, Now)));
        return outbox.Enqueue(new InvitationIssued(account.Id, account.Email.Value, account.Name.Value, account.Culture.Value, token, audience, customerName.OrNull()));
    }

    private void Audit(Maybe<Guid> actor, string action, string subject, Maybe<string> details) =>
        db.AuditLog.Add(new AuditEntry
        {
            At = Now,
            ActorId = actor.OrNull(),
            Action = action,
            Subject = subject,
            Details = details.OrNull(),
        });

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
            membership.Attributes.OrderLimit.Map(limit => limit.Amount).OrNull(),
            membership.Attributes.OrderLimit.Map(limit => limit.Currency.Code).OrNull(),
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

    /// <summary>The account as an acting operator: active and with operator attributes.</summary>
    public static Maybe<Subject.Operator> AsOperator(Account account) =>
        account.Operator
            .Where(_ => account.Status == AccountStatus.Active)
            .Map(attributes => new Subject.Operator(account.Id, account.Email, attributes));

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
