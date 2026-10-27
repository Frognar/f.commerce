using FCommerce.Access.Contracts;
using FCommerce.Access.Domain;
using FCommerce.Access.Persistence;
using FCommerce.Access.Security;
using FCommerce.Application.Messaging;
using FCommerce.Application.Results;
using FCommerce.Authorization.Core;
using FCommerce.Customers.Contracts;
using FCommerce.Infrastructure.Persistence;
using FCommerce.Settings.Contracts;
using FCommerce.SharedKernel.Contact;
using FCommerce.SharedKernel.Features;
using FCommerce.SharedKernel.Localization;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Results;
using FCommerce.SharedKernel.Validation;
using Microsoft.EntityFrameworkCore;

namespace FCommerce.Access.Handlers;

/// <summary>A person to give access to a customer.</summary>
internal sealed record CustomerInvitee(EmailAddress Email, PersonName Name, CultureCode Culture, MembershipAttributes Rights);

/// <summary>Granting, changing and revoking customer memberships under the ABAC grant rules.</summary>
internal sealed class MembershipManagement(
    AccessStore store,
    IQueryHandler<GetCustomer, Result<CustomerDetails, Error>> customers,
    IQueryHandler<GetPlatformSettings, PlatformSettingsView> settings)
{
    public async Task<Result<Guid, Error>> InviteAsync(Guid actorId, string customerCode, string email, string name, string culture, MembershipRightsView rights, CancellationToken cancellationToken)
    {
        var input = ((Func<EmailAddress, PersonName, CultureCode, MembershipAttributes, CustomerInvitee>)((e, n, c, r) => new CustomerInvitee(e, n, c, r)))
            .Apply(EmailAddress.Parse(email).ToValidated("email"))
            .Apply(PersonName.Parse(name).ToValidated("name"))
            .Apply(CultureCode.Parse(culture).ToValidated("culture"))
            .Apply(AccessStore.FromView(rights).ToValidated())
            .ToResult();

        return await input.BindAsync(async invitee =>
            await (await customers.HandleAsync(new GetCustomer(customerCode), cancellationToken)).BindAsync(async customer =>
                await (await StageAsync(actorId, customer, invitee, source: Maybe<MembershipSource>.None, cancellationToken)).MapAsync(async id =>
                {
                    await store.Db.SaveChangesAsync(cancellationToken);
                    return id;
                })));
    }

    /// <summary>
    /// Stages (without saving) access for a person to a customer: the account if it is new, the membership, the audit
    /// entry and the e-mail event. <paramref name="source"/> overrides the source derived from the actor.
    /// </summary>
    public async Task<Result<Guid, Error>> StageAsync(Guid actorId, CustomerDetails customer, CustomerInvitee invitee, Maybe<MembershipSource> source, CancellationToken cancellationToken)
    {
        var code = CustomerCodes.Of(customer);
        var actor = await SubjectAsync(actorId, customer.Code, cancellationToken);
        var row = await store.FindAsync(invitee.Email, cancellationToken);
        var existing = row.Map(r => r.ToDomain());
        var memberships = await existing.Match(
            async found => (await store.MembershipsOfAsync([found.Id], cancellationToken))[found.Id].ToList(),
            () => Task.FromResult(new List<Membership>()));
        var multiCustomer = (await settings.HandleAsync(new GetPlatformSettings(), cancellationToken)).IsOn(Feature.MultiCustomerAccounts.Value);
        var account = existing.ValueOr(() => Account.Invite(
            Guid.CreateVersion7(), invitee.Email, invitee.Name, invitee.Culture, Tokens.NewSecurityStamp(), Maybe<OperatorAttributes>.None));

        var admitted =
            from grantor in actor
            from attributes in Grants.Membership(grantor, customer.Code, Maybe<MembershipAttributes>.None, invitee.Rights, 0)
            from membership in MembershipRules.Admit(
                existing, memberships, multiCustomer, new Membership(account.Id, code, attributes, source.ValueOr(() => SourceOf(grantor))))
            select membership;

        return admitted.Bind(membership =>
        {
            row.Switch(_ => { }, () => store.Db.Accounts.Add(AccountRow.From(account, store.Now)));

            return Grant(account, membership, customer.Name).Map(_ =>
            {
                store.Audit(actorId, "membership.granted", Subject(customer.Code), Details(account.Email, membership));
                return account.Id;
            });
        });
    }

    /// <summary>Changes a member's rights; no <paramref name="rights"/> revokes the membership.</summary>
    public async Task<Result<Unit, Error>> ChangeAsync(Guid actorId, Guid userId, string customerCode, Maybe<MembershipAttributes> rights, CancellationToken cancellationToken)
    {
        var members = await store.Db.Memberships.Where(m => m.CustomerCode == customerCode).ToListAsync(cancellationToken);
        var found = await members.FirstOrNone(m => m.UserId == userId).Match(
            async row => (await store.FindAsync(userId, cancellationToken)).Map(account => (Row: row, Account: account.ToDomain())),
            () => Task.FromResult(Maybe<(MembershipRow Row, Account Account)>.None));
        return await found.Match(
            member => ChangeAsync(actorId, member.Row, member.Account, members, customerCode, rights, cancellationToken),
            () => Task.FromResult(Result.Fail<Unit, Error>(Error.NotFound("membership.not-found", "This person has no access to the customer."))));
    }

    private async Task<Result<Unit, Error>> ChangeAsync(
        Guid actorId, MembershipRow row, Account account, List<MembershipRow> members, string customerCode, Maybe<MembershipAttributes> rights, CancellationToken cancellationToken)
    {
        var current = row.ToDomain();
        var others = MembershipRules.OtherAdministrators(members.Select(m => m.ToDomain()), account.Id);
        var grantor = await SubjectAsync(actorId, customerCode, cancellationToken);

        // Changing keeps the membership with other rights; revoking removes it (an account without access to the customer).
        var applied = rights.Match(
            requested => grantor.Bind(g => Grants.Membership(g, customerCode, current.Attributes, requested, others)).Map(attributes =>
            {
                var changed = current with { Attributes = attributes };
                row.Apply(changed.Attributes);
                store.Audit(actorId, "membership.changed", Subject(customerCode), Details(account.Email, changed));
                return Unit.Value;
            }),
            () => grantor.Bind(g => Grants.MembershipRevocation(g, customerCode, current.Attributes, others)).Map(_ =>
            {
                store.Db.Memberships.Remove(row);
                store.Audit(actorId, "membership.revoked", Subject(customerCode), Details(account.Email, current));
                return Unit.Value;
            }));

        return await applied.MapAsync(async _ =>
        {
            await store.Db.SaveChangesAsync(cancellationToken);
            return Unit.Value;
        });
    }

    /// <summary>Stores the membership and tells the person: a new or still invited account gets an invitation link.</summary>
    public Result<Unit, Error> Grant(Account account, Membership membership, string customerName)
    {
        store.Db.Memberships.Add(MembershipRow.From(membership, store.Now));
        return account.State is AccountState.Invited
            ? store.Invite(account, "customer", customerName)
            : store.Enqueue(new MembershipGranted(account.Id, account.Email.Value, account.Name.Value, account.Culture.Value, customerName));
    }

    public static string Subject(string customerCode) => $"customer:{customerCode}";

    public static object Details(EmailAddress email, Membership membership) =>
        new { email = email.Value, source = membership.Source.Name, rights = AccessStore.ToRightsView(membership.Attributes) };

    /// <summary>The actor as the grant rules see them: an operator, or a member acting for this customer.</summary>
    private async Task<Result<Subject, Error>> SubjectAsync(Guid actorId, string customerCode, CancellationToken cancellationToken)
    {
        var actor = (await store.FindAsync(actorId, cancellationToken)).Map(row => row.ToDomain()).Where(a => a.State is AccountState.Active);
        return await actor.Match(
            active => AccessStore.AsOperator(active).Match(
                op => Task.FromResult(Result.Ok<Subject, Error>(op)),
                () => MemberAsync(actorId, customerCode, cancellationToken)),
            () => Task.FromResult(Result.Fail<Subject, Error>(InviteOperatorHandler.UnknownActor())));
    }

    private async Task<Result<Subject, Error>> MemberAsync(Guid actorId, string customerCode, CancellationToken cancellationToken) =>
        (await store.Db.Memberships.AsNoTracking().SingleOrNoneAsync(m => m.UserId == actorId && m.CustomerCode == customerCode, cancellationToken))
            .Map(row => (Subject)new Subject.CustomerUser(actorId, customerCode, row.ToDomain().Attributes))
            .ToResult(() => Error.Forbidden("grant.not-allowed", "You cannot change memberships of this customer."));

    private static MembershipSource SourceOf(Subject grantor) =>
        grantor is Subject.Operator ? MembershipSource.Operator : MembershipSource.CustomerAdmin;
}

/// <summary>The code of a customer read from the customers module (which validated it).</summary>
internal static class CustomerCodes
{
    public static Domain.CustomerCode Of(CustomerDetails customer) =>
        Domain.CustomerCode.Parse(customer.Code).Match(code => code, error => throw new InvalidOperationException($"The customers module returned an invalid customer code: {error.Message}"));
}

internal sealed class InviteCustomerUserHandler(MembershipManagement memberships) : ICommandHandler<InviteCustomerUser, Result<Guid, Error>>
{
    public Task<Result<Guid, Error>> HandleAsync(InviteCustomerUser command, CancellationToken cancellationToken = default) =>
        memberships.InviteAsync(command.ActorId, command.CustomerCode, command.Email, command.Name, command.Culture, command.Rights, cancellationToken);
}

internal sealed class ChangeMembershipHandler(MembershipManagement memberships) : ICommandHandler<ChangeMembership, Result<Unit, Error>>
{
    public Task<Result<Unit, Error>> HandleAsync(ChangeMembership command, CancellationToken cancellationToken = default) =>
        AccessStore.FromView(command.Rights).BindAsync(rights => memberships.ChangeAsync(command.ActorId, command.UserId, command.CustomerCode, rights, cancellationToken));
}

internal sealed class RevokeMembershipHandler(MembershipManagement memberships) : ICommandHandler<RevokeMembership, Result<Unit, Error>>
{
    public Task<Result<Unit, Error>> HandleAsync(RevokeMembership command, CancellationToken cancellationToken = default) =>
        memberships.ChangeAsync(command.ActorId, command.UserId, command.CustomerCode, Maybe<MembershipAttributes>.None, cancellationToken);
}

internal sealed class GetCustomerUsersHandler(AccessDbContext db) : IQueryHandler<GetCustomerUsers, IReadOnlyList<CustomerUserSummary>>
{
    public async Task<IReadOnlyList<CustomerUserSummary>> HandleAsync(GetCustomerUsers query, CancellationToken cancellationToken = default)
    {
        var members = await db.Memberships.AsNoTracking()
            .Where(m => m.CustomerCode == query.CustomerCode)
            .Join(db.Accounts.AsNoTracking(), m => m.UserId, a => a.Id, (membership, account) => new { membership, account })
            .OrderBy(x => x.account.Name)
            .ToListAsync(cancellationToken);

        return [.. members.Select(x =>
        {
            var membership = x.membership.ToDomain();
            var account = x.account.ToDomain();
            return new CustomerUserSummary(
                account.Id,
                account.Email.Value,
                account.Name.Value,
                account.Status.ToString(),
                membership.Source.Name,
                AccessStore.ToRightsView(membership.Attributes));
        })];
    }
}

internal sealed class GetAuditLogHandler(AccessDbContext db) : IQueryHandler<GetAuditLog, IReadOnlyList<AuditEntryView>>
{
    public const int MaxEntries = 200;

    public async Task<IReadOnlyList<AuditEntryView>> HandleAsync(GetAuditLog query, CancellationToken cancellationToken = default)
    {
        var entries = await db.AuditLog.AsNoTracking()
            .Where(e => e.Subject == query.Subject)
            .OrderByDescending(e => e.Id)
            .Take(Math.Clamp(query.Limit, 1, MaxEntries))
            .GroupJoin(db.Accounts.AsNoTracking(), e => e.ActorId, a => a.Id, (entry, actors) => new { entry, actors })
            .SelectMany(x => x.actors.DefaultIfEmpty(), (x, actor) => new { x.entry, Actor = actor == null ? null : actor.Name })
            .ToListAsync(cancellationToken);

        return [.. entries.OrderByDescending(x => x.entry.Id).Select(x => new AuditEntryView(x.entry.At, x.Actor, x.entry.Action, x.entry.Details))];
    }
}
