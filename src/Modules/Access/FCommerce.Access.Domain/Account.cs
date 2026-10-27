using FCommerce.Authorization.Core;
using FCommerce.SharedKernel.Contact;
using FCommerce.SharedKernel.Localization;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Persistence;
using FCommerce.SharedKernel.Results;
using FCommerce.SharedKernel.Validation;

namespace FCommerce.Access.Domain;

public sealed record PersonName : IPersistedValue<PersonName, string>
{
    public const int MaxLength = 200;

    private PersonName(string value) => Value = value;

    public string Value { get; }

    public static Result<PersonName, Error> Parse(string raw)
    {
        var value = raw.Trim();
        return value switch
        {
            "" => Error.Validation("name.empty", "Name is required."),
            { Length: > MaxLength } => Error.Validation("name.too-long", $"Name must not exceed {MaxLength} characters."),
            _ when value.Any(char.IsControl) => Error.Validation("name.invalid", "Name must not contain control characters."),
            _ => new PersonName(value),
        };
    }

    static PersonName IPersistedValue<PersonName, string>.Rehydrate(string raw) => new(raw);

    string IPersistedValue<PersonName, string>.Dehydrate() => Value;

    public override string ToString() => Value;
}

/// <summary>The stage of an account, as stored and shown.</summary>
public enum AccountStatus
{
    /// <summary>Created, waiting for the person to set a password through the invitation link.</summary>
    Invited,
    Active,
    Disabled,
}

/// <summary>Failed sign-ins of an active account: counted up to the limit, then a temporary lockout.</summary>
public abstract record SignInGuard
{
    private SignInGuard()
    {
    }

    public static SignInGuard Clear { get; } = new Open(0);

    /// <summary>Signing in is possible; <see cref="Failures"/> wrong passwords since the last success (fewer than the limit).</summary>
    public sealed record Open : SignInGuard
    {
        internal Open(int failures) => Failures = failures;

        public int Failures { get; }
    }

    /// <summary>Too many wrong passwords: no sign-in until <paramref name="Until"/>.</summary>
    public sealed record LockedOut(DateTimeOffset Until) : SignInGuard;
}

/// <summary>An account is invited (no password yet), active (with its password) or disabled.</summary>
public abstract record AccountState
{
    private AccountState()
    {
    }

    public sealed record Invited : AccountState;

    public sealed record Active(PasswordHash Password, SignInGuard Guard) : AccountState;

    public sealed record Disabled : AccountState;
}

/// <summary>
/// A person who can sign in. <see cref="SecurityStamp"/> changes whenever credentials or access change; sessions
/// carrying an older stamp are no longer valid (sign-out everywhere). <see cref="Operator"/> is set for the seller's
/// staff. Made only by <see cref="Invite"/> and the rules that change it.
/// </summary>
public sealed record Account : IPersistedValue<Account, Account.Stored>
{
    private Account(Guid id, EmailAddress email, PersonName name, CultureCode culture, AccountState state, SecurityStamp securityStamp, Maybe<OperatorAttributes> @operator)
    {
        Id = id;
        Email = email;
        Name = name;
        Culture = culture;
        State = state;
        SecurityStamp = securityStamp;
        Operator = @operator;
    }

    public Guid Id { get; }

    public EmailAddress Email { get; }

    public PersonName Name { get; }

    public CultureCode Culture { get; }

    public AccountState State { get; }

    public SecurityStamp SecurityStamp { get; }

    public Maybe<OperatorAttributes> Operator { get; }

    public AccountStatus Status =>
        State switch
        {
            AccountState.Invited => AccountStatus.Invited,
            AccountState.Active => AccountStatus.Active,
            _ => AccountStatus.Disabled,
        };

    public Maybe<PasswordHash> PasswordHash => State is AccountState.Active active ? active.Password : Maybe<PasswordHash>.None;

    public bool IsOperator => Operator.Match(_ => true, () => false);

    public bool IsLocked(DateTimeOffset now) => State is AccountState.Active { Guard: SignInGuard.LockedOut lockout } && lockout.Until > now;

    /// <summary>A new account waiting for its person to choose a password (a customer user or an operator).</summary>
    public static Account Invite(Guid id, EmailAddress email, PersonName name, CultureCode culture, SecurityStamp securityStamp, Maybe<OperatorAttributes> operatorAttributes) =>
        new(id, email, name, culture, new AccountState.Invited(), securityStamp, operatorAttributes);

    /// <summary>The name and language the person uses; a disabled account keeps its profile.</summary>
    public Result<Account, Error> ChangeProfile(PersonName name, CultureCode culture) =>
        State is AccountState.Disabled
            ? Error.Forbidden("account.disabled", "This account is disabled.")
            : new Account(Id, Email, name, culture, State, SecurityStamp, Operator);

    internal Account With(AccountState state, SecurityStamp securityStamp) => new(Id, Email, Name, Culture, state, securityStamp, Operator);

    internal Account With(AccountState.Active active) => new(Id, Email, Name, Culture, active, SecurityStamp, Operator);

    internal Account WithOperator(Maybe<OperatorAttributes> attributes, SecurityStamp securityStamp) => new(Id, Email, Name, Culture, State, securityStamp, attributes);

    /// <summary>The flat stored form of an account (one row).</summary>
    /// <param name="Password">Only an active account has one.</param>
    /// <param name="FailedSignIns">Of an active account that is not locked out.</param>
    /// <param name="LockedUntil">Of an active account that was locked out.</param>
    [RawInput]
    public sealed record Stored(
        Guid Id,
        EmailAddress Email,
        PersonName Name,
        CultureCode Culture,
        AccountStatus Status,
        Maybe<PasswordHash> Password,
        SecurityStamp SecurityStamp,
        int FailedSignIns,
        Maybe<DateTimeOffset> LockedUntil,
        Maybe<OperatorAttributes> Operator);

    static bool IPersistedValue<Account, Stored>.IsWellFormed(Stored raw) =>
        raw.Status switch
        {
            AccountStatus.Active => raw.Password.Exists(_ => true)
                && raw.FailedSignIns is >= 0 and < SignIn.MaxFailures
                && (raw.FailedSignIns == 0 || !raw.LockedUntil.Exists(_ => true)),
            AccountStatus.Invited or AccountStatus.Disabled => !raw.Password.Exists(_ => true) && raw.FailedSignIns == 0 && !raw.LockedUntil.Exists(_ => true),
            _ => false,
        };

    static Account IPersistedValue<Account, Stored>.Rehydrate(Stored raw) =>
        new(
            raw.Id,
            raw.Email,
            raw.Name,
            raw.Culture,
            raw.Status switch
            {
                AccountStatus.Invited => new AccountState.Invited(),
                AccountStatus.Active => raw.Password.Match<AccountState>(
                    password => new AccountState.Active(
                        password,
                        raw.LockedUntil.Match<SignInGuard>(until => new SignInGuard.LockedOut(until), () => new SignInGuard.Open(raw.FailedSignIns))),
                    () => new AccountState.Disabled()),
                _ => new AccountState.Disabled(),
            },
            raw.SecurityStamp,
            raw.Operator);

    Stored IPersistedValue<Account, Stored>.Dehydrate() =>
        new(
            Id,
            Email,
            Name,
            Culture,
            Status,
            PasswordHash,
            SecurityStamp,
            State is AccountState.Active { Guard: SignInGuard.Open open } ? open.Failures : 0,
            State is AccountState.Active { Guard: SignInGuard.LockedOut lockout } ? lockout.Until : Maybe<DateTimeOffset>.None,
            Operator);
}
