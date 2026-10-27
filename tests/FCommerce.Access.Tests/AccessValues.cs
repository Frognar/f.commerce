using FCommerce.Access.Domain;
using FCommerce.Authorization.Core;
using FCommerce.SharedKernel.Contact;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Persistence;
using FCommerce.Testing;

namespace FCommerce.Access.Tests;

/// <summary>Access values for the tests: parsed like the shell parses them, stored states read like rows are read.</summary>
internal static class AccessValues
{
    public static EmailAddress Email(string value) => EmailAddress.Parse(value).ShouldSucceed();

    public static SecurityStamp Stamp(string value = "stamp") => SecurityStamp.Parse(value).ShouldSucceed();

    public static PasswordHash Hash(string value = "hash") => PasswordHash.Parse(value).ShouldSucceed();

    public static SecretHash Secret(char digit = 'a') => SecretHash.Parse(new string(digit, SecretHash.Length)).ShouldSucceed();

    public static CustomerCode Customer(string code) => CustomerCode.Parse(code).ShouldSucceed();

    public static ErpContactId ContactId(string id) => ErpContactId.Parse(id).ShouldSucceed();

    /// <summary>The stored form read back, as the persistence mapping does (only well-formed data).</summary>
    public static T Rehydrate<T, TRaw>(TRaw raw)
        where T : IPersistedValue<T, TRaw>
        where TRaw : notnull =>
        T.IsWellFormed(raw) ? T.Rehydrate(raw) : throw new InvalidOperationException($"Ill-formed test data: {raw}");

    public static bool IsWellFormed<T, TRaw>(TRaw raw)
        where T : IPersistedValue<T, TRaw>
        where TRaw : notnull =>
        T.IsWellFormed(raw);

    public static Account.Stored Stored(
        string email = "jan@firma.pl", AccountStatus status = AccountStatus.Active, Maybe<OperatorAttributes> op = default, int failures = 0, Maybe<DateTimeOffset> lockedUntil = default, string stamp = "stamp") =>
        new(Guid.NewGuid(), Email(email), PersonName.Parse("Jan").ShouldSucceed(), Values.Culture("pl"), status,
            status == AccountStatus.Active ? Hash() : Maybe<PasswordHash>.None, Stamp(stamp), failures, lockedUntil, op);

    public static Account Account(
        string email = "jan@firma.pl", AccountStatus status = AccountStatus.Active, Maybe<OperatorAttributes> op = default, int failures = 0, Maybe<DateTimeOffset> lockedUntil = default, string stamp = "stamp") =>
        Rehydrate<Account, Account.Stored>(Stored(email, status, op, failures, lockedUntil, stamp));

    public static Account Invited(string email = "jan@firma.pl") =>
        Domain.Account.Invite(Guid.NewGuid(), Email(email), PersonName.Parse("Jan").ShouldSucceed(), Values.Culture("pl"), Stamp("old"), Maybe<OperatorAttributes>.None);

    /// <summary>The failures of an active account (none when locked out).</summary>
    public static int Failures(Account account) =>
        account.State is AccountState.Active { Guard: SignInGuard.Open open } ? open.Failures : 0;

    public static Maybe<DateTimeOffset> LockedUntil(Account account) =>
        account.State is AccountState.Active { Guard: SignInGuard.LockedOut lockout } ? lockout.Until : Maybe<DateTimeOffset>.None;
}
