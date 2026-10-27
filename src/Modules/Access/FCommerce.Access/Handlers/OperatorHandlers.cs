using FCommerce.Access.Contracts;
using FCommerce.Access.Domain;
using FCommerce.Access.Persistence;
using FCommerce.Access.Security;
using FCommerce.Application.Messaging;
using FCommerce.Application.Results;
using FCommerce.Authorization.Core;
using FCommerce.SharedKernel.Contact;
using FCommerce.SharedKernel.Localization;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Results;
using FCommerce.SharedKernel.Validation;
using Microsoft.EntityFrameworkCore;

namespace FCommerce.Access.Handlers;

/// <summary>Operator management, shared by the commands and the bootstrap of the first administrator.</summary>
internal sealed class OperatorManagement(AccessStore store)
{
    private sealed record Invitee(EmailAddress Email, PersonName Name, CultureCode Culture, OperatorAttributes Attributes);

    /// <summary>
    /// Invites an operator; no <paramref name="actor"/> means the system (bootstrap), which skips the grant rules.
    /// An existing account keeps its password and gets operator attributes; an account still waiting for its
    /// invitation gets a fresh link.
    /// </summary>
    public async Task<Result<Guid, Error>> InviteAsync(Maybe<Account> actor, string email, string name, string culture, OperatorView attributes, CancellationToken cancellationToken)
    {
        var input = ((Func<EmailAddress, PersonName, CultureCode, OperatorAttributes, Invitee>)((e, n, c, a) => new Invitee(e, n, c, a)))
            .Apply(EmailAddress.Parse(email).ToValidated("email"))
            .Apply(PersonName.Parse(name).ToValidated("name"))
            .Apply(CultureCode.Parse(culture).ToValidated("culture"))
            .Apply(AccessStore.FromView(attributes).ToValidated())
            .ToResult();

        return await input.BindAsync(async invitee =>
        {
            var row = await store.FindAsync(invitee.Email, cancellationToken);
            var existing = row.Map(r => r.ToDomain());
            var memberships = await existing.Match(
                async found => (await store.MembershipsOfAsync([found.Id], cancellationToken))[found.Id].ToList(),
                () => Task.FromResult(new List<Membership>()));
            var granted = await GrantAsync(actor, existing, invitee.Attributes, cancellationToken);
            var appointed = granted.Bind(rights => existing.Match(
                found => OperatorRules.Appoint(found, memberships, rights),
                () => Account.Invite(Guid.CreateVersion7(), invitee.Email, invitee.Name, invitee.Culture, Tokens.NewSecurityStamp(), rights)));
            return await appointed.BindAsync(async account =>
            {
                row.Switch(stored => stored.Apply(account), () => store.Db.Accounts.Add(AccountRow.From(account, store.Now)));

                var invited = account.State is AccountState.Invited ? store.Invite(account, "operator", Maybe<string>.None) : Unit.Value;
                return await invited.MapAsync(async _ =>
                {
                    store.Audit(actor.Map(a => a.Id), "operator.invited", $"account:{account.Id}", AccessStore.ToView(invitee.Attributes));
                    await store.Db.SaveChangesAsync(cancellationToken);
                    return account.Id;
                });
            });
        });
    }

    /// <summary>Changes an operator's rights; no <paramref name="attributes"/> revokes them.</summary>
    public async Task<Result<Unit, Error>> ChangeAsync(Account actor, Guid operatorId, Maybe<OperatorAttributes> attributes, CancellationToken cancellationToken)
    {
        var found = (await store.FindAsync(operatorId, cancellationToken))
            .Map(row => (Row: row, Account: row.ToDomain()))
            .Where(f => f.Account.IsOperator);
        return await found.Match(
            target => ChangeAsync(actor, target.Row, target.Account, attributes, cancellationToken),
            () => Task.FromResult(Result.Fail<Unit, Error>(Error.NotFound("operator.not-found", "The operator does not exist."))));
    }

    private async Task<Result<Unit, Error>> ChangeAsync(Account actor, AccountRow row, Account target, Maybe<OperatorAttributes> attributes, CancellationToken cancellationToken)
    {
        var applied = await attributes.Match(
            async changed => (await GrantAsync(actor, target, changed, cancellationToken))
                .Bind(granted => OperatorRules.Appoint(target, [], granted))
                .Map(appointed =>
                {
                    row.Apply(appointed);
                    store.Audit(actor.Id, "operator.changed", $"account:{target.Id}", AccessStore.ToView(changed));
                    return Unit.Value;
                }),
            async () =>
            {
                var otherManagers = await OtherManagersAsync(target, cancellationToken);
                return OperatorSubject(actor)
                    .Bind(subject => target.Operator.Match(
                        current => Grants.OperatorRevocation(subject, current, otherManagers),
                        () => Result.Ok<Unit, Error>(Unit.Value)))
                    .Map(_ =>
                    {
                        row.Apply(OperatorRules.Revoke(target, Tokens.NewSecurityStamp())); // signed out everywhere
                        store.Audit(actor.Id, "operator.revoked", $"account:{target.Id}");
                        return Unit.Value;
                    });
            });

        return await applied.MapAsync(async _ =>
        {
            await store.Db.SaveChangesAsync(cancellationToken);
            return Unit.Value;
        });
    }

    private async Task<Result<OperatorAttributes, Error>> GrantAsync(Maybe<Account> actor, Maybe<Account> target, OperatorAttributes requested, CancellationToken cancellationToken) =>
        await actor.Match(
            async grantor =>
            {
                var otherManagers = await OtherManagersAsync(target, cancellationToken);
                return OperatorSubject(grantor).Bind(subject => Grants.Operator(subject, target.Bind(t => t.Operator), requested, otherManagers));
            },
            () => Task.FromResult(Result.Ok<OperatorAttributes, Error>(requested)));

    /// <summary>Active operators other than <paramref name="target"/> who manage operators.</summary>
    private async Task<int> OtherManagersAsync(Maybe<Account> target, CancellationToken cancellationToken) =>
        (await store.Db.Accounts.AsNoTracking().Where(a => a.Operator != null).ToListAsync(cancellationToken))
            .Select(a => a.ToDomain())
            .Count(a => !target.Exists(t => t.Id == a.Id) && AccessStore.AsOperator(a).Exists(o => o.Attributes.CanManageOperators));

    private static Result<Subject.Operator, Error> OperatorSubject(Account account) =>
        AccessStore.AsOperator(account).ToResult(() => Error.Forbidden("grant.not-allowed", "Only operators can manage operators."));
}

internal sealed class InviteOperatorHandler(AccessStore store, OperatorManagement operators) : ICommandHandler<InviteOperator, Result<Guid, Error>>
{
    public async Task<Result<Guid, Error>> HandleAsync(InviteOperator command, CancellationToken cancellationToken = default) =>
        await (await store.FindAsync(command.ActorId, cancellationToken)).Match(
            row => operators.InviteAsync(row.ToDomain(), command.Email.OrEmpty(), command.Name.OrEmpty(), command.Culture.OrEmpty(), command.Attributes, cancellationToken),
            () => Task.FromResult(Result.Fail<Guid, Error>(UnknownActor())));

    internal static Error UnknownActor() => Error.Forbidden("grant.not-allowed", "Unknown actor.");
}

internal sealed class ChangeOperatorHandler(AccessStore store, OperatorManagement operators) : ICommandHandler<ChangeOperator, Result<Unit, Error>>
{
    public async Task<Result<Unit, Error>> HandleAsync(ChangeOperator command, CancellationToken cancellationToken = default) =>
        await (await store.FindAsync(command.ActorId, cancellationToken)).Match(
            row => AccessStore.FromView(command.Attributes).BindAsync(attributes => operators.ChangeAsync(row.ToDomain(), command.OperatorId, attributes, cancellationToken)),
            () => Task.FromResult(Result.Fail<Unit, Error>(InviteOperatorHandler.UnknownActor())));
}

internal sealed class RevokeOperatorHandler(AccessStore store, OperatorManagement operators) : ICommandHandler<RevokeOperator, Result<Unit, Error>>
{
    public async Task<Result<Unit, Error>> HandleAsync(RevokeOperator command, CancellationToken cancellationToken = default) =>
        await (await store.FindAsync(command.ActorId, cancellationToken)).Match(
            row => operators.ChangeAsync(row.ToDomain(), command.OperatorId, Maybe<OperatorAttributes>.None, cancellationToken),
            () => Task.FromResult(Result.Fail<Unit, Error>(InviteOperatorHandler.UnknownActor())));
}

internal sealed class GetOperatorsHandler(AccessDbContext db) : IQueryHandler<GetOperators, IReadOnlyList<OperatorSummary>>
{
    public async Task<IReadOnlyList<OperatorSummary>> HandleAsync(GetOperators query, CancellationToken cancellationToken = default) =>
        [.. (await db.Accounts.AsNoTracking().Where(a => a.Operator != null).OrderBy(a => a.Name).ToListAsync(cancellationToken))
            .Select(row => row.ToDomain())
            .Choose(account => account.Operator.Map(attributes => new OperatorSummary(account.Id, account.Email.Value, account.Name.Value, account.Status.ToString(), AccessStore.ToView(attributes))))];
}
