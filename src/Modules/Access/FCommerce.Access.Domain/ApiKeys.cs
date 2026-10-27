using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Persistence;
using FCommerce.SharedKernel.Results;

namespace FCommerce.Access.Domain;

/// <summary>The two parts of a key: the public identifier (to look the key up) and the secret (only its hash is stored).</summary>
public sealed record ApiKeyParts
{
    internal ApiKeyParts(ApiKeyId id, string secret)
    {
        Id = id;
        Secret = secret;
    }

    public ApiKeyId Id { get; }

    public string Secret { get; }
}

/// <summary>The shape of a key as a customer's system sends it: "fck_{id}_{secret}".</summary>
public static class ApiKeyFormat
{
    public const string Prefix = "fck";

    /// <summary>Lower-case hexadecimal characters of the identifier.</summary>
    public const int IdLength = ApiKeyId.Length;

    /// <summary>Base64url characters of 32 random bytes.</summary>
    public const int SecretLength = 43;

    public static string Compose(ApiKeyId id, string secret) => $"{Prefix}_{id.Value}_{secret}";

    public static Result<ApiKeyParts, Error> Parse(string raw) =>
        raw.Trim().Split('_', 3) is [Prefix, var id, { Length: SecretLength } secret] && secret.All(IsBase64Url)
            ? ApiKeyId.Parse(id).Map(parsed => new ApiKeyParts(parsed, secret))
            : Error.Validation("api-key.invalid", "The API key is not valid.");

    private static bool IsBase64Url(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_';
}

/// <summary>The validity periods a key can be issued for (in days); a key without one does not expire.</summary>
public enum KeyValidity
{
    Days30 = 30,
    Days90 = 90,
    Days365 = 365,
}

/// <summary>
/// A key a company user created for their system: it acts as that user for one customer, with the rights of the user's
/// membership. It stops working when revoked or expired, when the user loses the customer or the account is not active,
/// and when the account's security stamp changes (a new password ends it, like a session). Made only by
/// <see cref="Issue"/>; its use and revocation never precede its creation.
/// </summary>
/// <remarks><see cref="SecretHash"/> is the hash of the secret part; the secret itself is never stored.</remarks>
public sealed record ApiKey : IPersistedValue<ApiKey, ApiKey.Stored>
{
    /// <summary>The last use is recorded at most this often, so reading does not write on every request.</summary>
    public static readonly TimeSpan UseRecordInterval = TimeSpan.FromMinutes(5);

    private ApiKey(Stored stored) => Value = stored;

    private Stored Value { get; }

    public Guid Id => Value.Id;

    public Guid UserId => Value.UserId;

    public CustomerCode CustomerCode => Value.CustomerCode;

    public ApiKeyName Name => Value.Name;

    public ApiKeyId KeyId => Value.KeyId;

    public SecretHash SecretHash => Value.SecretHash;

    /// <summary>The stamp of the account when the key was issued.</summary>
    public SecurityStamp SecurityStamp => Value.SecurityStamp;

    public DateTimeOffset CreatedAt => Value.CreatedAt;

    public Maybe<DateTimeOffset> ExpiresAt => Value.ExpiresAt;

    public Maybe<DateTimeOffset> LastUsedAt => Value.LastUsedAt;

    public Maybe<DateTimeOffset> RevokedAt => Value.RevokedAt;

    public bool IsRevoked => RevokedAt.Exists(_ => true);

    public bool IsExpired(DateTimeOffset now) => ExpiresAt.Exists(at => at <= now);

    /// <summary>A validity period in days: one of <see cref="KeyValidity"/>, or none (no expiry).</summary>
    public static Result<Maybe<KeyValidity>, Error> Validity(Maybe<int> days) =>
        days.Match(
            value => Enum.IsDefined((KeyValidity)value)
                ? Result.Ok<Maybe<KeyValidity>, Error>((KeyValidity)value)
                : Error.Validation("api-key.validity-invalid", "Choose one of the offered validity periods.") with { Field = "validForDays" },
            () => Result.Ok<Maybe<KeyValidity>, Error>(Maybe<KeyValidity>.None));

    public static Result<ApiKey, Error> Issue(
        Guid id, Account owner, CustomerCode customerCode, ApiKeyName name, Maybe<KeyValidity> validity, ApiKeyId keyId, SecretHash secretHash, DateTimeOffset now) =>
        owner.State is AccountState.Active
            ? new ApiKey(new Stored(id, owner.Id, customerCode, name, keyId, secretHash, owner.SecurityStamp, now, validity.Map(days => now.AddDays((int)days)), Maybe<DateTimeOffset>.None, Maybe<DateTimeOffset>.None))
            : Error.Forbidden("api-key.account-inactive", "Only an active user can have an API key.");

    /// <summary>Revokes the key; a key that is already revoked keeps its first revocation time.</summary>
    public ApiKey Revoke(DateTimeOffset now) => IsRevoked ? this : new(Value with { RevokedAt = now });

    public bool ShouldRecordUse(DateTimeOffset now) => LastUsedAt.Match(last => now - last >= UseRecordInterval, () => true);

    public ApiKey RecordUse(DateTimeOffset now) => new(Value with { LastUsedAt = now });

    /// <summary>
    /// Whether the key may be used now. Every failure is the same to the caller ("invalid key"), so keys cannot be probed;
    /// the reason is for logs.
    /// </summary>
    public Result<Unit, ApiKeyFailure> Check(bool secretMatches, Account owner, bool ownerHasCustomer, DateTimeOffset now) =>
        (secretMatches, this) switch
        {
            (false, _) => ApiKeyFailure.SecretMismatch,
            (_, { IsRevoked: true }) => ApiKeyFailure.Revoked,
            _ when IsExpired(now) => ApiKeyFailure.Expired,
            _ when owner.State is not AccountState.Active || owner.SecurityStamp != SecurityStamp => ApiKeyFailure.OwnerChanged,
            _ when !ownerHasCustomer => ApiKeyFailure.NoCustomerAccess,
            _ => Unit.Value,
        };

    /// <summary>The stored form of a key (one row).</summary>
    public sealed record Stored(
        Guid Id,
        Guid UserId,
        CustomerCode CustomerCode,
        ApiKeyName Name,
        ApiKeyId KeyId,
        SecretHash SecretHash,
        SecurityStamp SecurityStamp,
        DateTimeOffset CreatedAt,
        Maybe<DateTimeOffset> ExpiresAt,
        Maybe<DateTimeOffset> LastUsedAt,
        Maybe<DateTimeOffset> RevokedAt);

    static bool IPersistedValue<ApiKey, Stored>.IsWellFormed(Stored raw) =>
        !raw.ExpiresAt.Exists(at => at <= raw.CreatedAt)
        && !raw.LastUsedAt.Exists(at => at < raw.CreatedAt)
        && !raw.RevokedAt.Exists(at => at < raw.CreatedAt);

    static ApiKey IPersistedValue<ApiKey, Stored>.Rehydrate(Stored raw) => new(raw);

    Stored IPersistedValue<ApiKey, Stored>.Dehydrate() => Value;
}

public enum ApiKeyFailure
{
    SecretMismatch,
    Revoked,
    Expired,
    OwnerChanged,
    NoCustomerAccess,
}
