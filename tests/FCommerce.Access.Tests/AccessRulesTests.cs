using FCommerce.Access.Domain;
using FCommerce.Access.Security;
using FCommerce.SharedKernel.Contact;
using FCommerce.SharedKernel.Localization;
using FCommerce.SharedKernel.Optional;
using FCommerce.Testing;

namespace FCommerce.Access.Tests;

public sealed class PasswordPolicyTests
{
    [Theory]
    [InlineData("correct horse battery")]
    [InlineData("Zażółć gęślą jaźń 2026")]
    public void Accepts_long_passwords_without_composition_rules(string password)
    {
        Assert.Equal(password, PasswordPolicy.Check(password, "jan@firma.pl").ShouldSucceed());
    }

    [Theory]
    [InlineData("short", "password.too-short")]
    [InlineData("aaaaaaaaaaaaaaaa", "password.too-simple")]
    [InlineData("kowalski-2026-secret", "password.contains-email")]
    public void Rejects_weak_passwords(string password, string code)
    {
        var error = PasswordPolicy.Check(password, "kowalski@firma.pl").ShouldFail();

        Assert.Equal(code, error.Code);
        Assert.Equal("password", error.Field);
    }
}

public sealed class SignInTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    private static Account Active(int failures = 0, Maybe<DateTimeOffset> lockedUntil = default) =>
        new(Guid.NewGuid(), EmailAddress.Parse("jan@firma.pl").ShouldSucceed(), PersonName.Parse("Jan").ShouldSucceed(), Values.Culture("pl"),
            AccountStatus.Active, "hash", "stamp", failures, lockedUntil, default);

    [Fact]
    public void Correct_password_signs_in_and_clears_failures()
    {
        var outcome = SignIn.Decide(Active(failures: 3), passwordMatches: true, Now);

        Assert.Equal(0, outcome.Result.ShouldSucceed().FailedSignIns);
        Assert.Equal(0, outcome.Updated.ShouldBeSome().FailedSignIns);
    }

    [Fact]
    public void Unknown_invited_or_disabled_accounts_fail_like_a_wrong_password()
    {
        var invited = Active() with { Status = AccountStatus.Invited, PasswordHash = default };
        var disabled = Active() with { Status = AccountStatus.Disabled };

        Assert.Equal(SignInFailure.InvalidCredentials, SignIn.Decide(default, false, Now).Result.ShouldFail());
        Assert.Equal(SignInFailure.InvalidCredentials, SignIn.Decide(invited, true, Now).Result.ShouldFail());
        Assert.Equal(SignInFailure.InvalidCredentials, SignIn.Decide(disabled, true, Now).Result.ShouldFail());
    }

    [Fact]
    public void Fifth_failure_locks_the_account_for_fifteen_minutes()
    {
        var fourth = SignIn.Decide(Active(failures: 3), false, Now);
        var fifth = SignIn.Decide(fourth.Updated, false, Now);

        Assert.Equal(SignInFailure.InvalidCredentials, fourth.Result.ShouldFail());
        Assert.Equal(SignInFailure.LockedOut, fifth.Result.ShouldFail());
        Assert.Equal(Now + SignIn.LockoutDuration, fifth.Updated.ShouldBeSome().LockedUntil.ShouldBeSome());
    }

    [Fact]
    public void A_locked_account_rejects_even_the_correct_password_until_the_lock_expires()
    {
        var locked = Active(lockedUntil: Now.AddMinutes(5));

        Assert.Equal(SignInFailure.LockedOut, SignIn.Decide(locked, true, Now).Result.ShouldFail());
        SignIn.Decide(locked, true, Now).Updated.ShouldBeNone();
        SignIn.Decide(locked, true, Now.AddMinutes(6)).Result.ShouldSucceed().LockedUntil.ShouldBeNone();
    }
}

public sealed class TokenAndCredentialTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Tokens_are_single_use_purpose_bound_and_expire()
    {
        var token = OneTimeToken.Issue(Guid.NewGuid(), TokenPurpose.PasswordReset, "hash", Now);
        var used = token.Redeem(TokenPurpose.PasswordReset, Now.AddMinutes(10)).ShouldSucceed();

        Assert.Equal(Now + OneTimeToken.PasswordResetLifetime, token.ExpiresAt);
        Assert.Equal("token.invalid", used.Redeem(TokenPurpose.PasswordReset, Now.AddMinutes(11)).ShouldFail().Code);
        Assert.Equal("token.invalid", token.Redeem(TokenPurpose.Invitation, Now).ShouldFail().Code);
        Assert.Equal("token.invalid", token.Redeem(TokenPurpose.PasswordReset, Now.AddHours(1)).ShouldFail().Code);
    }

    [Fact]
    public void Credential_changes_rotate_the_security_stamp()
    {
        var account = new Account(Guid.NewGuid(), EmailAddress.Parse("a@b.pl").ShouldSucceed(), PersonName.Parse("A").ShouldSucceed(), Values.Culture("pl"),
            AccountStatus.Invited, default, "old", 2, default, default);

        var activated = Credentials.Activate(account, "hash", "new").ShouldSucceed();

        Assert.Equal((AccountStatus.Active, "new", 0), (activated.Status, activated.SecurityStamp, activated.FailedSignIns));
        Assert.Equal("account.disabled", Credentials.Activate(Credentials.Disable(account, "x"), "hash", "new").ShouldFail().Code);
        Assert.Equal("account.not-active", Credentials.ChangePassword(account, "hash", "new").ShouldFail().Code);
    }
}

public sealed class SecretsTests
{
    [Fact]
    public void Password_hashes_are_salted_and_verify_only_the_right_password()
    {
        var first = PasswordHashing.Hash("correct horse battery");
        var second = PasswordHashing.Hash("correct horse battery");

        Assert.NotEqual(first, second);
        Assert.StartsWith("v1.600000.", first, StringComparison.Ordinal);
        Assert.True(PasswordHashing.Verify(first, "correct horse battery"));
        Assert.False(PasswordHashing.Verify(first, "correct horse batterY"));
        Assert.False(PasswordHashing.Verify("garbage", "anything"));
    }

    [Fact]
    public void Link_tokens_are_url_safe_and_stored_only_as_hashes()
    {
        var (token, hash) = Tokens.New();

        Assert.Matches("^[A-Za-z0-9_-]{43}$", token);
        Assert.Equal(Tokens.HashOf(token), hash);
        Assert.NotEqual(token, hash);
    }
}
