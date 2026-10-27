using FCommerce.Access.Domain;
using FCommerce.Access.Security;
using FCommerce.SharedKernel.Optional;
using FCommerce.Testing;
using static FCommerce.Access.Tests.AccessValues;

namespace FCommerce.Access.Tests;

public sealed class PasswordPolicyTests
{
    [Theory]
    [InlineData("correct horse battery")]
    [InlineData("Zażółć gęślą jaźń 2026")]
    public void Accepts_long_passwords_without_composition_rules(string password)
    {
        Assert.Equal(password, PasswordPolicy.Check(password, Email("jan@firma.pl")).ShouldSucceed().Value);
    }

    [Theory]
    [InlineData("short", "password.too-short")]
    [InlineData("aaaaaaaaaaaaaaaa", "password.too-simple")]
    [InlineData("kowalski-2026-secret", "password.contains-email")]
    public void Rejects_weak_passwords(string password, string code)
    {
        var error = PasswordPolicy.Check(password, Email("kowalski@firma.pl")).ShouldFail();

        Assert.Equal(code, error.Code);
        Assert.Equal("password", error.Field);
    }
}

public sealed class AccountTests
{
    [Fact]
    public void Only_an_active_account_has_a_password_and_sign_in_failures()
    {
        Assert.True(IsWellFormed<Account, Account.Stored>(Stored(failures: 4)));
        Assert.True(IsWellFormed<Account, Account.Stored>(Stored(status: AccountStatus.Invited)));
        Assert.False(IsWellFormed<Account, Account.Stored>(Stored() with { Password = Maybe<PasswordHash>.None }));
        Assert.False(IsWellFormed<Account, Account.Stored>(Stored(status: AccountStatus.Invited) with { Password = Hash() }));
        Assert.False(IsWellFormed<Account, Account.Stored>(Stored(status: AccountStatus.Invited, failures: 1)));
        Assert.False(IsWellFormed<Account, Account.Stored>(Stored(failures: SignIn.MaxFailures)));
        Assert.False(IsWellFormed<Account, Account.Stored>(Stored(failures: 2, lockedUntil: DateTimeOffset.UnixEpoch)));
    }

    [Fact]
    public void A_disabled_account_keeps_its_profile()
    {
        var disabled = Credentials.Disable(Account(), Stamp("new"));

        Assert.Equal("account.disabled", disabled.ChangeProfile(PersonName.Parse("Ewa").ShouldSucceed(), Values.Culture("en")).ShouldFail().Code);
        Assert.Equal("Ewa", Account().ChangeProfile(PersonName.Parse("Ewa").ShouldSucceed(), Values.Culture("en")).ShouldSucceed().Name.Value);
    }
}

public sealed class SignInTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Correct_password_signs_in_and_clears_failures()
    {
        var outcome = SignIn.Decide(Account(failures: 3), passwordMatches: true, Now);

        Assert.Equal(0, Failures(outcome.Result.ShouldSucceed()));
        Assert.Equal(0, Failures(outcome.Updated.ShouldBeSome()));
    }

    [Fact]
    public void Unknown_invited_or_disabled_accounts_fail_like_a_wrong_password()
    {
        Assert.Equal(SignInFailure.InvalidCredentials, SignIn.Decide(default, false, Now).Result.ShouldFail());
        Assert.Equal(SignInFailure.InvalidCredentials, SignIn.Decide(Invited(), true, Now).Result.ShouldFail());
        Assert.Equal(SignInFailure.InvalidCredentials, SignIn.Decide(Account(status: AccountStatus.Disabled), true, Now).Result.ShouldFail());
    }

    [Fact]
    public void Fifth_failure_locks_the_account_for_fifteen_minutes()
    {
        var fourth = SignIn.Decide(Account(failures: 3), false, Now);
        var fifth = SignIn.Decide(fourth.Updated, false, Now);

        Assert.Equal(SignInFailure.InvalidCredentials, fourth.Result.ShouldFail());
        Assert.Equal(SignInFailure.LockedOut, fifth.Result.ShouldFail());
        Assert.Equal(Now + SignIn.LockoutDuration, LockedUntil(fifth.Updated.ShouldBeSome()).ShouldBeSome());
    }

    [Fact]
    public void A_locked_account_rejects_even_the_correct_password_until_the_lock_expires()
    {
        var locked = Account(lockedUntil: Now.AddMinutes(5));

        Assert.Equal(SignInFailure.LockedOut, SignIn.Decide(locked, true, Now).Result.ShouldFail());
        SignIn.Decide(locked, true, Now).Updated.ShouldBeNone();
        LockedUntil(SignIn.Decide(locked, true, Now.AddMinutes(6)).Result.ShouldSucceed()).ShouldBeNone();
        Assert.Equal(1, Failures(SignIn.Decide(locked, false, Now.AddMinutes(6)).Updated.ShouldBeSome()));
    }
}

public sealed class TokenAndCredentialTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Tokens_are_single_use_purpose_bound_and_expire()
    {
        var token = OneTimeToken.Issue(Guid.NewGuid(), TokenPurpose.PasswordReset, Secret(), Now);
        var used = token.Redeem(TokenPurpose.PasswordReset, Now.AddMinutes(10)).ShouldSucceed();

        Assert.Equal(Now + OneTimeToken.PasswordResetLifetime, token.ExpiresAt);
        Assert.Equal("token.invalid", used.Redeem(TokenPurpose.PasswordReset, Now.AddMinutes(11)).ShouldFail().Code);
        Assert.Equal("token.invalid", token.Redeem(TokenPurpose.Invitation, Now).ShouldFail().Code);
        Assert.Equal("token.invalid", token.Redeem(TokenPurpose.PasswordReset, Now.AddHours(1)).ShouldFail().Code);
        Assert.False(IsWellFormed<OneTimeToken, OneTimeToken.Stored>(new(Guid.NewGuid(), TokenPurpose.Invitation, Secret(), Now, Now.AddSeconds(-1))));
    }

    [Fact]
    public void Credential_changes_rotate_the_security_stamp()
    {
        var invited = Invited();

        var activated = Credentials.Activate(invited, Hash(), Stamp("new")).ShouldSucceed();

        Assert.Equal((AccountStatus.Active, "new", 0), (activated.Status, activated.SecurityStamp.Value, Failures(activated)));
        Assert.Equal("account.disabled", Credentials.Activate(Credentials.Disable(invited, Stamp("x")), Hash(), Stamp("new")).ShouldFail().Code);
        Assert.Equal("account.not-active", Credentials.ChangePassword(invited, Hash(), Stamp("new")).ShouldFail().Code);
    }
}

public sealed class SecretsTests
{
    [Fact]
    public void Password_hashes_are_salted_and_verify_only_the_right_password()
    {
        var password = PasswordPolicy.Check("correct horse battery", Email("jan@firma.pl")).ShouldSucceed();
        var first = PasswordHashing.Hash(password).Value;
        var second = PasswordHashing.Hash(password).Value;

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
        Assert.Equal(Tokens.HashOf(token), hash.Value);
        Assert.NotEqual(token, hash.Value);
    }
}
