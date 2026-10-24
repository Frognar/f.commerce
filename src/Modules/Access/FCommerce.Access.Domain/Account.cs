using FCommerce.Authorization.Core;
using FCommerce.SharedKernel.Contact;
using FCommerce.SharedKernel.Localization;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Persistence;
using FCommerce.SharedKernel.Results;

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

public enum AccountStatus
{
    /// <summary>Created, waiting for the person to set a password through the invitation link.</summary>
    Invited,
    Active,
    Disabled,
}

/// <summary>
/// A person who can sign in. <see cref="SecurityStamp"/> changes whenever credentials or access change; sessions
/// carrying an older stamp are no longer valid (sign-out everywhere). <see cref="Operator"/> is set for the seller's staff.
/// </summary>
public sealed record Account(
    Guid Id,
    EmailAddress Email,
    PersonName Name,
    CultureCode Culture,
    AccountStatus Status,
    Maybe<string> PasswordHash,
    string SecurityStamp,
    int FailedSignIns,
    Maybe<DateTimeOffset> LockedUntil,
    Maybe<OperatorAttributes> Operator)
{
    public bool IsLocked(DateTimeOffset now) => LockedUntil.Exists(until => until > now);

    public bool IsOperator => Operator.Match(_ => true, () => false);
}
