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
            membership.CustomerCode.Value,
            membership.Attributes.CanOrder,
            ToView(membership.Attributes.OrderScope),
            membership.Attributes.OrderLimit.Map(limit => limit.Value.Amount).OrNull(),
            membership.Attributes.OrderLimit.Map(limit => limit.Value.Currency.Code).OrNull(),
            membership.Attributes.CanViewDocuments,
            membership.Attributes.CanManageUsers,
            Codes(membership.Attributes.DeliveryAddresses).OrNull());

    public static MembershipRightsView ToRightsView(MembershipAttributes attributes) =>
        new(
            attributes.CanOrder,
            ToView(attributes.OrderScope),
            attributes.CanViewDocuments,
            attributes.CanManageUsers,
            attributes.OrderLimit.Map(limit => limit.Value.Amount).OrNull(),
            attributes.OrderLimit.Map(limit => limit.Value.Currency.Code).OrNull(),
            Codes(attributes.DeliveryAddresses).OrNull());

    public static Result<MembershipAttributes, Error> FromView(MembershipRightsView view) =>
        from scope in view.OrderScope switch
        {
            "own" => Result.Ok<OrderScope, Error>(OrderScope.Own),
            "company" => OrderScope.Company,
            _ => Error.Validation("membership.order-scope-invalid", "Order scope must be 'own' or 'company'.") with { Field = "orderScope" },
        }
        from attributes in MembershipRights.Parse(
            view.CanOrder, scope, view.CanViewDocuments, view.CanManageUsers, view.OrderLimit.ToMaybe(), view.OrderLimitCurrency.OrEmpty(), view.DeliveryAddressCodes.ToMaybe())
        select attributes;

    /// <summary>The allowed codes; none for all addresses.</summary>
    private static Maybe<IReadOnlyList<string>> Codes(DeliveryAddressScope scope) =>
        scope switch
        {
            DeliveryAddressScope.OnlyAddresses only => Maybe.Some<IReadOnlyList<string>>([.. only.Codes]),
            DeliveryAddressScope.AllAddresses => Maybe<IReadOnlyList<string>>.None,
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "A delivery address scope without a view."),
        };

    private static string ToView(OrderScope scope) =>
        scope switch
        {
            OrderScope.Company => "company",
            OrderScope.Own => "own",
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "An order scope without a name."),
        };

    /// <summary>The account as an acting operator: active and with operator attributes.</summary>
    public static Maybe<Subject.Operator> AsOperator(Account account) =>
        account.Operator
            .Where(_ => account.State is AccountState.Active)
            .Map(attributes => new Subject.Operator(account.Id, account.Email, attributes));

    public static OperatorView ToView(OperatorAttributes attributes) =>
        new(
            attributes.CanManageSettings,
            attributes.CanManageOperators,
            attributes.CanDecideRegistrations,
            attributes.CustomerScope switch
            {
                CustomerScope.All => "all",
                CustomerScope.AsSalesperson => "as-salesperson",
                _ => throw new ArgumentOutOfRangeException(nameof(attributes), attributes.CustomerScope, "A customer scope without a name."),
            });

    public static Result<OperatorAttributes, Error> FromView(OperatorView view) =>
        view.CustomerScope switch
        {
            "all" => new OperatorAttributes(view.CanManageSettings, view.CanManageOperators, view.CanDecideRegistrations, CustomerScope.All),
            "as-salesperson" => new OperatorAttributes(view.CanManageSettings, view.CanManageOperators, view.CanDecideRegistrations, CustomerScope.AsSalesperson),
            _ => Error.Validation("operator.scope-invalid", "Customer scope must be 'all' or 'as-salesperson'.") with { Field = "customerScope" },
        };
}
