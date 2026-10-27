using FCommerce.SharedKernel.Contact;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Persistence;
using FCommerce.SharedKernel.Results;

namespace FCommerce.Access.Domain;

/// <summary>Password rules after NIST SP 800-63B: length matters, composition rules do not.</summary>
public static class PasswordPolicy
{
    public const int MinLength = 12;
    public const int MaxLength = 128;

    public static Result<ValidPassword, Error> Check(string password, EmailAddress email)
    {
        if (password.Length < MinLength)
        {
            return Error.Validation("password.too-short", $"The password must have at least {MinLength} characters.") with { Field = "password" };
        }

        if (password.Length > MaxLength)
        {
            return Error.Validation("password.too-long", $"The password must not exceed {MaxLength} characters.") with { Field = "password" };
        }

        if (password.Distinct().Count() < 4)
        {
            return Error.Validation("password.too-simple", "The password repeats too few characters.") with { Field = "password" };
        }

        var localPart = email.Value.Split('@')[0];
        return localPart.Length >= 4 && password.Contains(localPart, StringComparison.OrdinalIgnoreCase)
            ? Error.Validation("password.contains-email", "The password must not contain your e-mail address.") with { Field = "password" }
            : new ValidPassword(password);
    }
}

/// <summary>Why signing in failed; deliberately coarse so responses do not reveal whether an account exists.</summary>
public enum SignInFailure
{
    InvalidCredentials,
    LockedOut,
}

/// <summary>
/// Sign-in as a pure decision: the shell looks up the account and verifies the password hash, this decides the outcome
/// and the account's new state (failure counting and temporary lockout).
/// </summary>
public static class SignIn
{
    public const int MaxFailures = 5;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    /// <summary>The outcome with the account to store (failures are stored too).</summary>
    public sealed record Outcome(Result<Account, SignInFailure> Result, Maybe<Account> Updated);

    /// <param name="account">The account with the e-mail signed in with, if there is one.</param>
    public static Outcome Decide(Maybe<Account> account, bool passwordMatches, DateTimeOffset now) =>
        account
            .Bind(a => a.State is AccountState.Active active ? Maybe.Some((Account: a, State: active)) : Maybe<(Account Account, AccountState.Active State)>.None)
            .Match(
                found => Decide(found.Account, found.State, passwordMatches, now),
                () => new Outcome(SignInFailure.InvalidCredentials, Maybe<Account>.None));

    private static Outcome Decide(Account account, AccountState.Active active, bool passwordMatches, DateTimeOffset now)
    {
        if (account.IsLocked(now))
        {
            return new Outcome(SignInFailure.LockedOut, Maybe<Account>.None);
        }

        if (!passwordMatches)
        {
            var failures = (active.Guard is SignInGuard.Open open ? open.Failures : 0) + 1;
            var updated = account.With(active with
            {
                Guard = failures >= MaxFailures ? new SignInGuard.LockedOut(now + LockoutDuration) : new SignInGuard.Open(failures),
            });
            return new Outcome(updated.IsLocked(now) ? SignInFailure.LockedOut : SignInFailure.InvalidCredentials, updated);
        }

        var signedIn = account.With(active with { Guard = SignInGuard.Clear });
        return new Outcome(signedIn, signedIn == account ? Maybe<Account>.None : signedIn);
    }
}

public enum TokenPurpose
{
    Invitation,
    PasswordReset,
}

/// <summary>A one-time link token as stored: only its hash, never the token itself. It expires a fixed time after it was issued.</summary>
public sealed record OneTimeToken : IPersistedValue<OneTimeToken, OneTimeToken.Stored>
{
    public static readonly TimeSpan InvitationLifetime = TimeSpan.FromDays(7);
    public static readonly TimeSpan PasswordResetLifetime = TimeSpan.FromHours(1);

    private OneTimeToken(Guid accountId, TokenPurpose purpose, SecretHash hash, DateTimeOffset issuedAt, Maybe<DateTimeOffset> usedAt)
    {
        AccountId = accountId;
        Purpose = purpose;
        Hash = hash;
        IssuedAt = issuedAt;
        UsedAt = usedAt;
    }

    public Guid AccountId { get; }

    public TokenPurpose Purpose { get; }

    public SecretHash Hash { get; }

    public DateTimeOffset IssuedAt { get; }

    public Maybe<DateTimeOffset> UsedAt { get; }

    public DateTimeOffset ExpiresAt => IssuedAt + (Purpose == TokenPurpose.Invitation ? InvitationLifetime : PasswordResetLifetime);

    public static OneTimeToken Issue(Guid accountId, TokenPurpose purpose, SecretHash hash, DateTimeOffset now) =>
        new(accountId, purpose, hash, now, Maybe<DateTimeOffset>.None);

    /// <summary>Marks the token used, if it is unused and not expired.</summary>
    public Result<OneTimeToken, Error> Redeem(TokenPurpose purpose, DateTimeOffset now) =>
        UsedAt.Match(_ => true, () => purpose != Purpose || now >= ExpiresAt)
            ? Error.Validation("token.invalid", "The link is invalid or has expired.")
            : new OneTimeToken(AccountId, Purpose, Hash, IssuedAt, now);

    /// <param name="UsedAt">Never before it was issued.</param>
    public sealed record Stored(Guid AccountId, TokenPurpose Purpose, SecretHash Hash, DateTimeOffset IssuedAt, Maybe<DateTimeOffset> UsedAt);

    static bool IPersistedValue<OneTimeToken, Stored>.IsWellFormed(Stored raw) =>
        Enum.IsDefined(raw.Purpose) && !raw.UsedAt.Exists(used => used < raw.IssuedAt);

    static OneTimeToken IPersistedValue<OneTimeToken, Stored>.Rehydrate(Stored raw) => new(raw.AccountId, raw.Purpose, raw.Hash, raw.IssuedAt, raw.UsedAt);

    Stored IPersistedValue<OneTimeToken, Stored>.Dehydrate() => new(AccountId, Purpose, Hash, IssuedAt, UsedAt);
}

/// <summary>Credential changes: each one rotates the security stamp, which signs the account out everywhere.</summary>
public static class Credentials
{
    /// <summary>Accepting an invitation activates the account with its first password (again, for one already active).</summary>
    public static Result<Account, Error> Activate(Account account, PasswordHash password, SecurityStamp newStamp) =>
        account.State is AccountState.Disabled
            ? Error.Forbidden("account.disabled", "This account is disabled.")
            : account.With(new AccountState.Active(password, SignInGuard.Clear), newStamp);

    public static Result<Account, Error> ChangePassword(Account account, PasswordHash password, SecurityStamp newStamp) =>
        account.State is AccountState.Active
            ? account.With(new AccountState.Active(password, SignInGuard.Clear), newStamp)
            : Error.Forbidden("account.not-active", "This account is not active.");

    public static Account Disable(Account account, SecurityStamp newStamp) => account.With(new AccountState.Disabled(), newStamp);
}
