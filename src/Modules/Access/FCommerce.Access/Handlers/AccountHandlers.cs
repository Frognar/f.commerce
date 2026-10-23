using FCommerce.Access.Contracts;
using FCommerce.Access.Domain;
using FCommerce.Access.Persistence;
using FCommerce.Access.Security;
using FCommerce.Application.Messaging;
using FCommerce.Application.Results;
using FCommerce.SharedKernel.Contact;
using FCommerce.SharedKernel.Localization;
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
        var email = EmailAddress.Parse(command.Email).Match(e => (EmailAddress?)e, _ => null);
        var row = email is null ? null : await store.FindAsync(email, cancellationToken);
        var account = row?.ToDomain();

        // Always run the hash check, so the response time does not reveal whether the account exists.
        var matches = PasswordHashing.Verify(account?.PasswordHash ?? PasswordHashing.Decoy, command.Password ?? string.Empty)
            && account?.PasswordHash is not null;
        var outcome = SignInRules.Decide(account, matches, store.Now);

        if (outcome.Updated is { } updated)
        {
            row!.Apply(updated);
            if (updated.IsLocked(store.Now) && !account!.IsLocked(store.Now))
            {
                store.Audit(null, "account.locked-out", $"account:{updated.Id}");
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
        var account = (await store.Db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == query.UserId, cancellationToken))?.ToDomain();
        if (account is not { Status: AccountStatus.Active } || account.SecurityStamp != query.SecurityStamp)
        {
            return Error.NotFound("access.session-ended", "The session has ended.");
        }

        var memberships = await store.Db.Memberships.AsNoTracking().Where(m => m.UserId == account.Id).OrderBy(m => m.CustomerCode).ToListAsync(cancellationToken);
        return new SessionView(
            account.Id,
            account.Email.Value,
            account.Name.Value,
            account.Culture.Value,
            account.Operator is null ? null : AccessStore.ToView(account.Operator),
            [.. memberships.Select(m => AccessStore.ToView(m.ToDomain()))]);
    }
}

internal sealed class GetInvitationHandler(AccessStore store) : IQueryHandler<GetInvitation, Result<InvitationView, Error>>
{
    public async Task<Result<InvitationView, Error>> HandleAsync(GetInvitation query, CancellationToken cancellationToken = default)
    {
        var token = await store.FindTokenAsync(query.Token, cancellationToken);
        var valid = token?.ToDomain().Redeem(TokenPurpose.Invitation, store.Now).Match(_ => true, _ => false) ?? false;
        var account = valid ? await store.FindAsync(token!.AccountId, cancellationToken) : null;
        return account is not null && account.Status != nameof(AccountStatus.Disabled)
            ? new InvitationView(account.Email, account.Name)
            : Error.Validation("token.invalid", "The link is invalid or has expired.");
    }
}

internal sealed class AcceptInvitationHandler(AccessStore store) : ICommandHandler<AcceptInvitation, Result<SignedIn, Error>>
{
    public async Task<Result<SignedIn, Error>> HandleAsync(AcceptInvitation command, CancellationToken cancellationToken = default)
    {
        var tokenRow = await store.FindTokenAsync(command.Token, cancellationToken);
        var accountRow = tokenRow is null ? null : await store.FindAsync(tokenRow.AccountId, cancellationToken);
        if (tokenRow is null || accountRow is null)
        {
            return Error.Validation("token.invalid", "The link is invalid or has expired.");
        }

        var account = accountRow.ToDomain();
        var result =
            from redeemed in tokenRow.ToDomain().Redeem(TokenPurpose.Invitation, store.Now)
            from password in PasswordPolicy.Check(command.Password, account.Email.Value)
            from activated in Credentials.Activate(account, PasswordHashing.Hash(password), Tokens.NewSecurityStamp())
            select (redeemed, activated);

        return await result.MapAsync(async changed =>
        {
            tokenRow.UsedAt = changed.redeemed.UsedAt;
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
        var email = EmailAddress.Parse(command.Email).Match(e => (EmailAddress?)e, _ => null);
        var account = email is null ? null : (await store.FindAsync(email, cancellationToken))?.ToDomain();
        if (account is not { Status: AccountStatus.Active })
        {
            return Unit.Value; // same answer for unknown accounts
        }

        var (token, hash) = Tokens.New();
        store.Db.Tokens.Add(TokenRow.From(OneTimeToken.Issue(account.Id, TokenPurpose.PasswordReset, hash, store.Now)));
        return await store.Enqueue(new PasswordResetRequested(account.Id, account.Email.Value, account.Name.Value, account.Culture.Value, token))
            .MapAsync(async _ =>
            {
                store.Audit(null, "account.password-reset-requested", $"account:{account.Id}");
                await store.Db.SaveChangesAsync(cancellationToken);
                return Unit.Value;
            });
    }
}

internal sealed class ResetPasswordHandler(AccessStore store) : ICommandHandler<ResetPassword, Result<Unit, Error>>
{
    public async Task<Result<Unit, Error>> HandleAsync(ResetPassword command, CancellationToken cancellationToken = default)
    {
        var tokenRow = await store.FindTokenAsync(command.Token, cancellationToken);
        var accountRow = tokenRow is null ? null : await store.FindAsync(tokenRow.AccountId, cancellationToken);
        if (tokenRow is null || accountRow is null)
        {
            return Error.Validation("token.invalid", "The link is invalid or has expired.");
        }

        var account = accountRow.ToDomain();
        var result =
            from redeemed in tokenRow.ToDomain().Redeem(TokenPurpose.PasswordReset, store.Now)
            from password in PasswordPolicy.Check(command.Password, account.Email.Value)
            from changed in Credentials.ChangePassword(account, PasswordHashing.Hash(password), Tokens.NewSecurityStamp())
            select (redeemed, changed);

        return await result.MapAsync(async done =>
        {
            tokenRow.UsedAt = done.redeemed.UsedAt;
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
        var row = await store.FindAsync(command.UserId, cancellationToken);
        var account = row?.ToDomain();
        if (account?.PasswordHash is null || !PasswordHashing.Verify(account.PasswordHash, command.CurrentPassword ?? string.Empty))
        {
            return Error.Validation("password.current-invalid", "The current password is incorrect.") with { Field = "currentPassword" };
        }

        var result =
            from password in PasswordPolicy.Check(command.NewPassword, account.Email.Value)
            from changed in Credentials.ChangePassword(account, PasswordHashing.Hash(password), Tokens.NewSecurityStamp())
            select changed;

        return await result.MapAsync(async changed =>
        {
            row!.Apply(changed);
            store.Audit(account.Id, "account.password-changed", $"account:{account.Id}");
            await store.Db.SaveChangesAsync(cancellationToken);
            return new SignedIn(changed.Id, changed.SecurityStamp, changed.Culture.Value);
        });
    }
}

internal sealed class ChangeProfileHandler(AccessStore store) : ICommandHandler<ChangeProfile, Result<Unit, Error>>
{
    public async Task<Result<Unit, Error>> HandleAsync(ChangeProfile command, CancellationToken cancellationToken = default)
    {
        var row = await store.FindAsync(command.UserId, cancellationToken);
        if (row is null)
        {
            return Error.NotFound("account.not-found", "The account does not exist.");
        }

        var profile = ((Func<PersonName, CultureCode, (PersonName Name, CultureCode Culture)>)((name, culture) => (name, culture)))
            .Apply(PersonName.Parse(command.Name).ToValidated("name"))
            .Apply(CultureCode.Parse(command.Culture).ToValidated("culture"))
            .ToResult();

        return await profile.MapAsync(async changed =>
        {
            row.Apply(row.ToDomain() with { Name = changed.Name, Culture = changed.Culture });
            await store.Db.SaveChangesAsync(cancellationToken);
            return Unit.Value;
        });
    }
}
