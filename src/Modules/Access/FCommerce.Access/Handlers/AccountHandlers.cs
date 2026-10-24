using FCommerce.Access.Contracts;
using FCommerce.Access.Domain;
using FCommerce.Access.Persistence;
using FCommerce.Access.Security;
using FCommerce.Application.Messaging;
using FCommerce.Application.Results;
using FCommerce.Infrastructure.Persistence;
using FCommerce.SharedKernel.Contact;
using FCommerce.SharedKernel.Localization;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Results;
using FCommerce.SharedKernel.Validation;
using Microsoft.EntityFrameworkCore;
using SignInCommand = FCommerce.Access.Contracts.SignIn;
using SignInRules = FCommerce.Access.Domain.SignIn;

namespace FCommerce.Access.Handlers;

internal sealed class SignInHandler(AccessStore store) : ICommandHandler<SignInCommand, Result<SignedIn, Error>>
{
    public async Task<Result<SignedIn, Error>> HandleAsync(SignInCommand command, CancellationToken cancellationToken = default)
    {
        var row = await EmailAddress.Parse(command.Email.OrEmpty()).ToMaybe().Match(
            email => store.FindAsync(email, cancellationToken),
            () => Task.FromResult(Maybe<AccountRow>.None));
        var account = row.Map(r => r.ToDomain());
        var hash = account.Bind(a => a.PasswordHash);

        // Always run the hash check, so the response time does not reveal whether the account exists.
        var matches = PasswordHashing.Verify(hash.ValueOr(PasswordHashing.Decoy), command.Password.OrEmpty())
            && hash.Match(_ => true, () => false);
        var outcome = SignInRules.Decide(account, matches, store.Now);

        foreach (var (stored, before, updated) in (from r in row from a in account from u in outcome.Updated select (r, a, u)).AsEnumerable())
        {
            stored.Apply(updated);
            if (updated.IsLocked(store.Now) && !before.IsLocked(store.Now))
            {
                store.Audit(AccessStore.SystemActor, "account.locked-out", $"account:{updated.Id}");
            }

            await store.Db.SaveChangesAsync(cancellationToken);
        }

        return outcome.Result.Match(
            signedIn => Result.Ok<SignedIn, Error>(new SignedIn(signedIn.Id, signedIn.SecurityStamp, signedIn.Culture.Value)),
            failure => failure == SignInFailure.LockedOut
                ? Error.Forbidden("access.locked-out", "Too many failed attempts. Try again later.")
                : Error.Forbidden("access.invalid-credentials", "Invalid e-mail or password."));
    }
}

internal sealed class GetSessionHandler(AccessStore store) : IQueryHandler<GetSession, Result<SessionView, Error>>
{
    public async Task<Result<SessionView, Error>> HandleAsync(GetSession query, CancellationToken cancellationToken = default)
    {
        var account = (await store.Db.Accounts.AsNoTracking().SingleOrNoneAsync(a => a.Id == query.UserId, cancellationToken))
            .Map(row => row.ToDomain())
            .Where(a => a.Status == AccountStatus.Active && a.SecurityStamp == query.SecurityStamp);

        return await account.Match(
            async active =>
            {
                var memberships = await store.Db.Memberships.AsNoTracking().Where(m => m.UserId == active.Id).OrderBy(m => m.CustomerCode).ToListAsync(cancellationToken);
                return Result.Ok<SessionView, Error>(new SessionView(
                    active.Id,
                    active.Email.Value,
                    active.Name.Value,
                    active.Culture.Value,
                    active.Operator.Map(AccessStore.ToView).OrNull(),
                    [.. memberships.Select(m => AccessStore.ToView(m.ToDomain()))]));
            },
            () => Task.FromResult(Result.Fail<SessionView, Error>(Error.NotFound("access.session-ended", "The session has ended."))));
    }
}

internal sealed class GetInvitationHandler(AccessStore store) : IQueryHandler<GetInvitation, Result<InvitationView, Error>>
{
    public async Task<Result<InvitationView, Error>> HandleAsync(GetInvitation query, CancellationToken cancellationToken = default) =>
        (await store.FindTokenAccountAsync(query.Token.OrEmpty(), cancellationToken))
            .Where(found => found.Token.ToDomain().Redeem(TokenPurpose.Invitation, store.Now).Match(_ => true, _ => false))
            .Where(found => found.Account.Status != nameof(AccountStatus.Disabled))
            .Map(found => new InvitationView(found.Account.Email, found.Account.Name))
            .ToResult(InvalidToken);

    internal static Error InvalidToken() => Error.Validation("token.invalid", "The link is invalid or has expired.");
}

internal sealed class AcceptInvitationHandler(AccessStore store) : ICommandHandler<AcceptInvitation, Result<SignedIn, Error>>
{
    public async Task<Result<SignedIn, Error>> HandleAsync(AcceptInvitation command, CancellationToken cancellationToken = default) =>
        await (await store.FindTokenAccountAsync(command.Token.OrEmpty(), cancellationToken)).Match(
            found => AcceptAsync(found.Token, found.Account, command.Password.OrEmpty(), cancellationToken),
            () => Task.FromResult(Result.Fail<SignedIn, Error>(GetInvitationHandler.InvalidToken())));

    private Task<Result<SignedIn, Error>> AcceptAsync(TokenRow tokenRow, AccountRow accountRow, string newPassword, CancellationToken cancellationToken)
    {
        var account = accountRow.ToDomain();
        var result =
            from redeemed in tokenRow.ToDomain().Redeem(TokenPurpose.Invitation, store.Now)
            from password in PasswordPolicy.Check(newPassword, account.Email.Value)
            from activated in Credentials.Activate(account, PasswordHashing.Hash(password), Tokens.NewSecurityStamp())
            select (redeemed, activated);

        return result.MapAsync(async changed =>
        {
            tokenRow.UsedAt = changed.redeemed.UsedAt.OrNull();
            accountRow.Apply(changed.activated);
            store.Audit(account.Id, "account.invitation-accepted", $"account:{account.Id}");
            await store.Db.SaveChangesAsync(cancellationToken);
            return new SignedIn(account.Id, changed.activated.SecurityStamp, account.Culture.Value);
        });
    }
}

internal sealed class RequestPasswordResetHandler(AccessStore store) : ICommandHandler<RequestPasswordReset, Result<Unit, Error>>
{
    public async Task<Result<Unit, Error>> HandleAsync(RequestPasswordReset command, CancellationToken cancellationToken = default)
    {
        var account = (await EmailAddress.Parse(command.Email.OrEmpty()).ToMaybe().Match(
                email => store.FindAsync(email, cancellationToken),
                () => Task.FromResult(Maybe<AccountRow>.None)))
            .Map(row => row.ToDomain())
            .Where(a => a.Status == AccountStatus.Active);

        // The same answer for unknown accounts.
        return await account.Match(active => IssueAsync(active, cancellationToken), () => Task.FromResult(Result.Ok<Unit, Error>(Unit.Value)));
    }

    private Task<Result<Unit, Error>> IssueAsync(Account account, CancellationToken cancellationToken)
    {
        var (token, hash) = Tokens.New();
        store.Db.Tokens.Add(TokenRow.From(OneTimeToken.Issue(account.Id, TokenPurpose.PasswordReset, hash, store.Now)));
        return store.Enqueue(new PasswordResetRequested(account.Id, account.Email.Value, account.Name.Value, account.Culture.Value, token))
            .MapAsync(async _ =>
            {
                store.Audit(AccessStore.SystemActor, "account.password-reset-requested", $"account:{account.Id}");
                await store.Db.SaveChangesAsync(cancellationToken);
                return Unit.Value;
            });
    }
}

internal sealed class ResetPasswordHandler(AccessStore store) : ICommandHandler<ResetPassword, Result<Unit, Error>>
{
    public async Task<Result<Unit, Error>> HandleAsync(ResetPassword command, CancellationToken cancellationToken = default) =>
        await (await store.FindTokenAccountAsync(command.Token.OrEmpty(), cancellationToken)).Match(
            found => ResetAsync(found.Token, found.Account, command.Password.OrEmpty(), cancellationToken),
            () => Task.FromResult(Result.Fail<Unit, Error>(GetInvitationHandler.InvalidToken())));

    private Task<Result<Unit, Error>> ResetAsync(TokenRow tokenRow, AccountRow accountRow, string newPassword, CancellationToken cancellationToken)
    {
        var account = accountRow.ToDomain();
        var result =
            from redeemed in tokenRow.ToDomain().Redeem(TokenPurpose.PasswordReset, store.Now)
            from password in PasswordPolicy.Check(newPassword, account.Email.Value)
            from changed in Credentials.ChangePassword(account, PasswordHashing.Hash(password), Tokens.NewSecurityStamp())
            select (redeemed, changed);

        return result.MapAsync(async done =>
        {
            tokenRow.UsedAt = done.redeemed.UsedAt.OrNull();
            accountRow.Apply(done.changed);
            store.Audit(account.Id, "account.password-reset", $"account:{account.Id}");
            await store.Db.SaveChangesAsync(cancellationToken);
            return Unit.Value;
        });
    }
}

internal sealed class ChangePasswordHandler(AccessStore store) : ICommandHandler<ChangePassword, Result<SignedIn, Error>>
{
    public async Task<Result<SignedIn, Error>> HandleAsync(ChangePassword command, CancellationToken cancellationToken = default)
    {
        var current = command.CurrentPassword.OrEmpty();
        var verified = (await store.FindAsync(command.UserId, cancellationToken))
            .Map(row => (Row: row, Account: row.ToDomain()))
            .Where(found => found.Account.PasswordHash.Exists(hash => PasswordHashing.Verify(hash, current)));

        return await verified.Match(
            found => ChangeAsync(found.Row, found.Account, command.NewPassword.OrEmpty(), cancellationToken),
            () => Task.FromResult(Result.Fail<SignedIn, Error>(
                Error.Validation("password.current-invalid", "The current password is incorrect.") with { Field = "currentPassword" })));
    }

    private Task<Result<SignedIn, Error>> ChangeAsync(AccountRow row, Account account, string newPassword, CancellationToken cancellationToken)
    {
        var result =
            from password in PasswordPolicy.Check(newPassword, account.Email.Value)
            from changed in Credentials.ChangePassword(account, PasswordHashing.Hash(password), Tokens.NewSecurityStamp())
            select changed;

        return result.MapAsync(async changed =>
        {
            row.Apply(changed);
            store.Audit(account.Id, "account.password-changed", $"account:{account.Id}");
            await store.Db.SaveChangesAsync(cancellationToken);
            return new SignedIn(changed.Id, changed.SecurityStamp, changed.Culture.Value);
        });
    }
}

internal sealed class ChangeProfileHandler(AccessStore store) : ICommandHandler<ChangeProfile, Result<Unit, Error>>
{
    public async Task<Result<Unit, Error>> HandleAsync(ChangeProfile command, CancellationToken cancellationToken = default) =>
        await (await store.FindAsync(command.UserId, cancellationToken)).Match(
            row => ChangeAsync(row, command, cancellationToken),
            () => Task.FromResult(Result.Fail<Unit, Error>(Error.NotFound("account.not-found", "The account does not exist."))));

    private Task<Result<Unit, Error>> ChangeAsync(AccountRow row, ChangeProfile command, CancellationToken cancellationToken)
    {
        var profile = ((Func<PersonName, CultureCode, (PersonName Name, CultureCode Culture)>)((name, culture) => (name, culture)))
            .Apply(PersonName.Parse(command.Name.OrEmpty()).ToValidated("name"))
            .Apply(CultureCode.Parse(command.Culture.OrEmpty()).ToValidated("culture"))
            .ToResult();

        return profile.MapAsync(async changed =>
        {
            row.Apply(row.ToDomain() with { Name = changed.Name, Culture = changed.Culture });
            await store.Db.SaveChangesAsync(cancellationToken);
            return Unit.Value;
        });
    }
}
