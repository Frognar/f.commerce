using FCommerce.Access.Contracts;
using FCommerce.Access.Domain;
using FCommerce.Access.Persistence;
using FCommerce.Access.Security;
using FCommerce.Application.Messaging;
using FCommerce.Application.Results;
using FCommerce.Authorization.Core;
using FCommerce.SharedKernel.Contact;
using FCommerce.SharedKernel.Localization;
using FCommerce.SharedKernel.Results;
using FCommerce.SharedKernel.Validation;
using Microsoft.EntityFrameworkCore;

namespace FCommerce.Access.Handlers;

/// <summary>Operator management, shared by the commands and the bootstrap of the first administrator.</summary>
internal sealed class OperatorManagement(AccessStore store)
{
    private sealed record Invitee(EmailAddress Email, PersonName Name, CultureCode Culture, OperatorAttributes Attributes);

    /// <summary>
    /// Invites an operator; <paramref name="actor"/> null means the system (bootstrap), which skips the grant rules.
    /// An existing account keeps its password and gets operator attributes; an account still waiting for its
    /// invitation gets a fresh link.
    /// </summary>
    public async Task<Result<Guid, Error>> InviteAsync(Account? actor, string email, string name, string culture, OperatorView attributes, CancellationToken cancellationToken)
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
            var existing = row?.ToDomain();
            var granted = await GrantAsync(actor, existing, invitee.Attributes, cancellationToken);
            return await granted.BindAsync(async attributes =>
            {
                var account = existing is null
                    ? new Account(Guid.CreateVersion7(), invitee.Email, invitee.Name, invitee.Culture, AccountStatus.Invited, null, Tokens.NewSecurityStamp(), 0, null, attributes)
                    : existing with { Operator = attributes };

                if (row is null)
                {
                    store.Db.Accounts.Add(AccountRow.From(account, store.Now));
                }
                else
                {
                    row.Apply(account);
                }

                var invited = account.Status == AccountStatus.Invited ? store.Invite(account, "operator", null) : Unit.Value;
                return await invited.MapAsync(async _ =>
                {
                    store.Audit(actor?.Id, "operator.invited", $"account:{account.Id}", AccessStore.ToView(attributes));
                    await store.Db.SaveChangesAsync(cancellationToken);
                    return account.Id;
                });
            });
        });
    }

    public async Task<Result<Unit, Error>> ChangeAsync(Account actor, Guid operatorId, OperatorAttributes? attributes, CancellationToken cancellationToken)
    {
        var row = await store.FindAsync(operatorId, cancellationToken);
        if (row?.ToDomain() is not { Operator: not null } target)
        {
            return Error.NotFound("operator.not-found", "The operator does not exist.");
        }

        // Revoking is checked like a change to "no rights at all".
        var requested = attributes ?? new OperatorAttributes(false, false, false, CustomerScope.AsSalesperson);
        return await (await GrantAsync(actor, target, requested, cancellationToken)).MapAsync(async _ =>
        {
            var changed = attributes is null
                ? target with { Operator = null, SecurityStamp = Tokens.NewSecurityStamp() } // signed out everywhere
                : target with { Operator = attributes };
            row.Apply(changed);
            store.Audit(actor.Id, attributes is null ? "operator.revoked" : "operator.changed", $"account:{target.Id}", attributes is null ? null : AccessStore.ToView(attributes));
            await store.Db.SaveChangesAsync(cancellationToken);
            return Unit.Value;
        });
    }

    private async Task<Result<OperatorAttributes, Error>> GrantAsync(Account? actor, Account? target, OperatorAttributes requested, CancellationToken cancellationToken)
    {
        if (actor is null)
        {
            return requested;
        }

        var otherManagers = (await store.Db.Accounts.AsNoTracking().Where(a => a.Operator != null && a.Status == nameof(AccountStatus.Active)).ToListAsync(cancellationToken))
            .Select(a => a.ToDomain())
            .Count(a => a.Id != target?.Id && a.Operator!.CanManageOperators);
        return AccessStore.AsOperator(actor) is { } subject
            ? Grants.Operator(subject, target?.Operator, requested, otherManagers)
            : Error.Forbidden("grant.not-allowed", "Only operators can manage operators.");
    }
}

internal sealed class InviteOperatorHandler(AccessStore store, OperatorManagement operators) : ICommandHandler<InviteOperator, Result<Guid, Error>>
{
    public async Task<Result<Guid, Error>> HandleAsync(InviteOperator command, CancellationToken cancellationToken = default) =>
        (await store.FindAsync(command.ActorId, cancellationToken))?.ToDomain() is { } actor
            ? await operators.InviteAsync(actor, command.Email, command.Name, command.Culture, command.Attributes, cancellationToken)
            : Error.Forbidden("grant.not-allowed", "Unknown actor.");
}

internal sealed class ChangeOperatorHandler(AccessStore store, OperatorManagement operators) : ICommandHandler<ChangeOperator, Result<Unit, Error>>
{
    public async Task<Result<Unit, Error>> HandleAsync(ChangeOperator command, CancellationToken cancellationToken = default) =>
        (await store.FindAsync(command.ActorId, cancellationToken))?.ToDomain() is { } actor
            ? await AccessStore.FromView(command.Attributes).BindAsync(attributes => operators.ChangeAsync(actor, command.OperatorId, attributes, cancellationToken))
            : Error.Forbidden("grant.not-allowed", "Unknown actor.");
}

internal sealed class RevokeOperatorHandler(AccessStore store, OperatorManagement operators) : ICommandHandler<RevokeOperator, Result<Unit, Error>>
{
    public async Task<Result<Unit, Error>> HandleAsync(RevokeOperator command, CancellationToken cancellationToken = default) =>
        (await store.FindAsync(command.ActorId, cancellationToken))?.ToDomain() is { } actor
            ? await operators.ChangeAsync(actor, command.OperatorId, null, cancellationToken)
            : Error.Forbidden("grant.not-allowed", "Unknown actor.");
}

internal sealed class GetOperatorsHandler(AccessDbContext db) : IQueryHandler<GetOperators, IReadOnlyList<OperatorSummary>>
{
    public async Task<IReadOnlyList<OperatorSummary>> HandleAsync(GetOperators query, CancellationToken cancellationToken = default) =>
        [.. (await db.Accounts.AsNoTracking().Where(a => a.Operator != null).OrderBy(a => a.Name).ToListAsync(cancellationToken))
            .Select(row => row.ToDomain())
            .Select(account => new OperatorSummary(account.Id, account.Email.Value, account.Name.Value, account.Status.ToString(), AccessStore.ToView(account.Operator!)))];
}
