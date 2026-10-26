using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Results;

namespace FCommerce.Access.Domain;

/// <summary>The two parts of a key: the public identifier (to look the key up) and the secret (only its hash is stored).</summary>
public readonly record struct ApiKeyParts(string Id, string Secret);

/// <summary>The shape of a key as a customer's system sends it: "fck_{id}_{secret}".</summary>
public static class ApiKeyFormat
{
    public const string Prefix = "fck";

    /// <summary>Lower-case hexadecimal characters of the identifier.</summary>
    public const int IdLength = 12;

    /// <summary>Base64url characters of 32 random bytes.</summary>
    public const int SecretLength = 43;

    public static string Compose(string id, string secret) => $"{Prefix}_{id}_{secret}";

    public static Result<ApiKeyParts, Error> Parse(string raw) =>
        raw.Trim().Split('_', 3) is [Prefix, { Length: IdLength } id, { Length: SecretLength } secret] && id.All(IsHex) && secret.All(IsBase64Url)
            ? new ApiKeyParts(id, secret)
            : Error.Validation("api-key.invalid", "The API key is not valid.");

    private static bool IsHex(char c) => c is >= '0' and <= '9' or >= 'a' and <= 'f';

    private static bool IsBase64Url(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_';
}

/// <summary>
/// A key a company user created for their system: it acts as that user for one customer, with the rights of the user's
/// membership. It stops working when revoked or expired, when the user loses the customer or the account is not active,
/// and when the account's security stamp changes (a new password ends it, like a session).
/// </summary>
/// <param name="KeyId">The public identifier from the key.</param>
/// <param name="SecretHash">Hash of the secret part; the secret itself is never stored.</param>
/// <param name="SecurityStamp">The stamp of the account when the key was issued.</param>
public sealed record ApiKey(
    Guid Id,
    Guid UserId,
    string CustomerCode,
    string Name,
    string KeyId,
    string SecretHash,
    string SecurityStamp,
    DateTimeOffset CreatedAt,
    Maybe<DateTimeOffset> ExpiresAt,
    Maybe<DateTimeOffset> LastUsedAt,
    Maybe<DateTimeOffset> RevokedAt)
{
    public const int MaxNameLength = 100;

    /// <summary>The validity periods a key can be issued for; none: the key does not expire.</summary>
    public static readonly IReadOnlyList<int> ValidityDays = [30, 90, 365];

    /// <summary>The last use is recorded at most this often, so reading does not write on every request.</summary>
    public static readonly TimeSpan UseRecordInterval = TimeSpan.FromMinutes(5);

    public bool IsRevoked => RevokedAt.Exists(_ => true);

    public bool IsExpired(DateTimeOffset now) => ExpiresAt.Exists(at => at <= now);

    public static Result<ApiKey, Error> Issue(
        Guid id, Account owner, string customerCode, string name, Maybe<int> validForDays, string keyId, string secretHash, DateTimeOffset now)
    {
        var trimmed = name.Trim();
        return trimmed switch
        {
            "" => Error.Validation("api-key.name-empty", "Name the key.") with { Field = "name" },
            { Length: > MaxNameLength } => Error.Validation("api-key.name-too-long", $"The name must not exceed {MaxNameLength} characters.") with { Field = "name" },
            _ when trimmed.Any(char.IsControl) => Error.Validation("api-key.name-invalid", "The name must not contain control characters.") with { Field = "name" },
            _ when validForDays.Exists(days => !ValidityDays.Contains(days)) => Error.Validation("api-key.validity-invalid", "Choose one of the offered validity periods.") with { Field = "validForDays" },
            _ when owner.Status != AccountStatus.Active => Error.Forbidden("api-key.account-inactive", "Only an active user can have an API key."),
            _ => new ApiKey(id, owner.Id, customerCode, trimmed, keyId, secretHash, owner.SecurityStamp, now, validForDays.Map(days => now.AddDays(days)), Maybe<DateTimeOffset>.None, Maybe<DateTimeOffset>.None),
        };
    }

    /// <summary>Revokes the key; a key that is already revoked keeps its first revocation time.</summary>
    public ApiKey Revoke(DateTimeOffset now) => IsRevoked ? this : this with { RevokedAt = now };

    public bool ShouldRecordUse(DateTimeOffset now) => LastUsedAt.Match(last => now - last >= UseRecordInterval, () => true);

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
            _ when owner.Status != AccountStatus.Active || owner.SecurityStamp != SecurityStamp => ApiKeyFailure.OwnerChanged,
            _ when !ownerHasCustomer => ApiKeyFailure.NoCustomerAccess,
            _ => Unit.Value,
        };
}

public enum ApiKeyFailure
{
    SecretMismatch,
    Revoked,
    Expired,
    OwnerChanged,
    NoCustomerAccess,
}
