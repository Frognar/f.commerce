using FCommerce.Access.Domain;
using FCommerce.Access.Security;
using FCommerce.SharedKernel.Contact;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Results;
using FCommerce.Testing;
using static FCommerce.Access.Tests.AccessValues;

namespace FCommerce.Access.Tests;

public sealed class ApiKeyFormatTests
{
    private static readonly ApiKeyId Id = ApiKeyId.Parse("0123456789ab").ShouldSucceed();
    private static readonly string Secret = new('A', ApiKeyFormat.SecretLength);

    [Fact]
    public void A_composed_key_parses_back_into_its_parts()
    {
        var parts = ApiKeyFormat.Parse(ApiKeyFormat.Compose(Id, Secret)).ShouldSucceed();

        Assert.Equal(Id, parts.Id);
        Assert.Equal(Secret, parts.Secret);
    }

    [Fact]
    public void The_secret_may_contain_underscores()
    {
        var secret = "a_b-" + new string('c', ApiKeyFormat.SecretLength - 4);

        Assert.Equal(secret, ApiKeyFormat.Parse(ApiKeyFormat.Compose(Id, secret)).ShouldSucceed().Secret);
    }

    [Theory]
    [InlineData("")]
    [InlineData("fck")]
    [InlineData("fck_0123456789ab")]
    [InlineData("xyz_0123456789ab_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("fck_0123456789AB_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("fck_0123456789ab_short")]
    [InlineData("fck_0123456789ab_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA!")]
    public void A_malformed_key_does_not_parse(string raw)
    {
        Assert.Equal("api-key.invalid", ApiKeyFormat.Parse(raw).ShouldFail().Code);
    }

    [Fact]
    public void Generated_keys_have_the_documented_shape_and_match_their_hash_only()
    {
        var (id, secret, hash) = ApiKeySecrets.New();

        var parts = ApiKeyFormat.Parse(ApiKeyFormat.Compose(id, secret)).ShouldSucceed();
        Assert.Equal(secret, parts.Secret);
        Assert.True(ApiKeySecrets.Matches(hash.Value, secret));
        Assert.False(ApiKeySecrets.Matches(hash.Value, new string('B', ApiKeyFormat.SecretLength)));
        Assert.NotEqual(secret, hash.Value);
    }
}

public sealed class ApiKeyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private static readonly ApiKeyId KeyId = ApiKeyId.Parse("0123456789ab").ShouldSucceed();

    private static Account Owner(AccountStatus status = AccountStatus.Active, string stamp = "stamp") => Account(status: status, stamp: stamp);

    private static Result<ApiKey, Error> Issue(Account owner, string name = " ERP integration ", Maybe<int> validForDays = default) =>
        from parsed in ApiKeyName.Parse(name)
        from validity in ApiKey.Validity(validForDays)
        from key in ApiKey.Issue(Guid.NewGuid(), owner, Customer("C1"), parsed, validity, KeyId, Secret(), Now)
        select key;

    private static ApiKey Issued(Account owner, Maybe<int> validForDays = default) => Issue(owner, validForDays: validForDays).ShouldSucceed();

    [Fact]
    public void An_issued_key_belongs_to_the_owner_with_their_current_stamp()
    {
        var owner = Owner();
        var key = Issued(owner, 90);

        Assert.Equal("ERP integration", key.Name.Value);
        Assert.Equal(owner.Id, key.UserId);
        Assert.Equal("stamp", key.SecurityStamp.Value);
        Assert.Equal(Now.AddDays(90), key.ExpiresAt.ShouldBeSome());
        Assert.False(key.IsRevoked);
    }

    [Fact]
    public void A_key_without_validity_does_not_expire()
    {
        Assert.False(Issued(Owner()).IsExpired(Now.AddYears(50)));
    }

    [Theory]
    [InlineData("", "api-key.name-empty")]
    [InlineData("   ", "api-key.name-empty")]
    [InlineData("tab\tname", "api-key.name-invalid")]
    public void A_key_needs_a_clean_name(string name, string code)
    {
        Assert.Equal(code, Issue(Owner(), name).ShouldFail().Code);
    }

    [Fact]
    public void The_name_is_limited_and_the_validity_is_one_of_the_offered()
    {
        Assert.Equal("api-key.name-too-long", Issue(Owner(), new string('x', 101)).ShouldFail().Code);
        Assert.Equal("api-key.validity-invalid", Issue(Owner(), validForDays: 7).ShouldFail().Code);
    }

    [Fact]
    public void Only_an_active_user_gets_a_key()
    {
        Assert.Equal("api-key.account-inactive", Issue(Owner(AccountStatus.Disabled)).ShouldFail().Code);
        Assert.Equal("api-key.account-inactive", Issue(Invited()).ShouldFail().Code);
    }

    [Fact]
    public void A_valid_key_may_be_used()
    {
        var owner = Owner();

        Issued(owner).Check(secretMatches: true, owner, ownerHasCustomer: true, Now.AddDays(1)).ShouldSucceed();
    }

    [Fact]
    public void Every_reason_to_refuse_a_key_is_distinguished_for_the_logs()
    {
        var owner = Owner();
        var key = Issued(owner, 30);

        Assert.Equal(ApiKeyFailure.SecretMismatch, key.Check(false, owner, true, Now).ShouldFail());
        Assert.Equal(ApiKeyFailure.Revoked, key.Revoke(Now).Check(true, owner, true, Now).ShouldFail());
        Assert.Equal(ApiKeyFailure.Expired, key.Check(true, owner, true, Now.AddDays(30)).ShouldFail());
        Assert.Equal(ApiKeyFailure.OwnerChanged, key.Check(true, Credentials.ChangePassword(owner, Hash("other"), Stamp("new")).ShouldSucceed(), true, Now).ShouldFail());
        Assert.Equal(ApiKeyFailure.OwnerChanged, key.Check(true, Credentials.Disable(owner, owner.SecurityStamp), true, Now).ShouldFail());
        Assert.Equal(ApiKeyFailure.NoCustomerAccess, key.Check(true, owner, false, Now).ShouldFail());
    }

    [Fact]
    public void Revoking_twice_keeps_the_first_revocation()
    {
        var key = Issued(Owner()).Revoke(Now);

        Assert.Equal(key, key.Revoke(Now.AddHours(1)));
        Assert.Equal(Now, key.RevokedAt.ShouldBeSome());
    }

    [Fact]
    public void The_last_use_is_recorded_at_most_every_few_minutes()
    {
        var key = Issued(Owner());

        Assert.True(key.ShouldRecordUse(Now));
        var used = key.RecordUse(Now);
        Assert.False(used.ShouldRecordUse(Now.AddMinutes(4)));
        Assert.True(used.ShouldRecordUse(Now.AddMinutes(5)));
    }

    [Fact]
    public void A_stored_key_is_never_used_revoked_or_expiring_before_its_creation()
    {
        var stored = new ApiKey.Stored(
            Guid.NewGuid(), Guid.NewGuid(), Customer("C1"), ApiKeyName.Parse("k").ShouldSucceed(), KeyId, Secret(), Stamp(), Now, default, default, default);

        Assert.True(IsWellFormed<ApiKey, ApiKey.Stored>(stored));
        Assert.False(IsWellFormed<ApiKey, ApiKey.Stored>(stored with { ExpiresAt = Now }));
        Assert.False(IsWellFormed<ApiKey, ApiKey.Stored>(stored with { LastUsedAt = Now.AddSeconds(-1) }));
        Assert.False(IsWellFormed<ApiKey, ApiKey.Stored>(stored with { RevokedAt = Now.AddDays(-1) }));
    }
}
