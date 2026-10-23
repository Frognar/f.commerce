using FCommerce.SharedKernel.Results;

namespace FCommerce.Access.Domain;

/// <summary>Password rules after NIST SP 800-63B: length matters, composition rules do not.</summary>
public static class PasswordPolicy
{
    public const int MinLength = 12;
    public const int MaxLength = 128;

    public static Result<string, Error> Check(string? password, string email)
    {
        if (password is null || password.Length < MinLength)
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

        var localPart = email.Split('@')[0];
        return localPart.Length >= 4 && password.Contains(localPart, StringComparison.OrdinalIgnoreCase)
            ? Error.Validation("password.contains-email", "The password must not contain your e-mail address.") with { Field = "password" }
            : password;
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
    public sealed record Outcome(Result<Account, SignInFailure> Result, Account? Updated);

    public static Outcome Decide(Account? account, bool passwordMatches, DateTimeOffset now)
    {
        if (account is null || account.Status != AccountStatus.Active || account.PasswordHash is null)
        {
            return new Outcome(SignInFailure.InvalidCredentials, null);
        }

        if (account.IsLocked(now))
        {
            return new Outcome(SignInFailure.LockedOut, null);
        }

        if (!passwordMatches)
        {
            var failures = account.FailedSignIns + 1;
            var updated = failures >= MaxFailures
                ? account with { FailedSignIns = 0, LockedUntil = now + LockoutDuration }
                : account with { FailedSignIns = failures };
            return new Outcome(updated.IsLocked(now) ? SignInFailure.LockedOut : SignInFailure.InvalidCredentials, updated);
        }

        var signedIn = account with { FailedSignIns = 0, LockedUntil = null };
        return new Outcome(signedIn, signedIn == account ? null : signedIn);
    }
}

public enum TokenPurpose
{
    Invitation,
    PasswordReset,
}

/// <summary>A one-time link token as stored: only its hash, never the token itself.</summary>
public sealed record OneTimeToken(Guid AccountId, TokenPurpose Purpose, string Hash, DateTimeOffset ExpiresAt, DateTimeOffset? UsedAt)
{
    public static readonly TimeSpan InvitationLifetime = TimeSpan.FromDays(7);
    public static readonly TimeSpan PasswordResetLifetime = TimeSpan.FromHours(1);

    public static OneTimeToken Issue(Guid accountId, TokenPurpose purpose, string hash, DateTimeOffset now) =>
        new(accountId, purpose, hash, now + (purpose == TokenPurpose.Invitation ? InvitationLifetime : PasswordResetLifetime), null);

    /// <summary>Marks the token used, if it is unused and not expired.</summary>
    public Result<OneTimeToken, Error> Redeem(TokenPurpose purpose, DateTimeOffset now) =>
        purpose != Purpose || UsedAt is not null || now >= ExpiresAt
            ? Error.Validation("token.invalid", "The link is invalid or has expired.")
            : this with { UsedAt = now };
}

/// <summary>Credential changes: each one rotates the security stamp, which signs the account out everywhere.</summary>
public static class Credentials
{
    /// <summary>Accepting an invitation activates the account with its first password.</summary>
    public static Result<Account, Error> Activate(Account account, string passwordHash, string newStamp) =>
        account.Status == AccountStatus.Disabled
            ? Error.Forbidden("account.disabled", "This account is disabled.")
            : account with { Status = AccountStatus.Active, PasswordHash = passwordHash, SecurityStamp = newStamp, FailedSignIns = 0, LockedUntil = null };

    public static Result<Account, Error> ChangePassword(Account account, string passwordHash, string newStamp) =>
        account.Status != AccountStatus.Active
            ? Error.Forbidden("account.not-active", "This account is not active.")
            : account with { PasswordHash = passwordHash, SecurityStamp = newStamp, FailedSignIns = 0, LockedUntil = null };

    public static Account Disable(Account account, string newStamp) =>
        account with { Status = AccountStatus.Disabled, SecurityStamp = newStamp };
}
