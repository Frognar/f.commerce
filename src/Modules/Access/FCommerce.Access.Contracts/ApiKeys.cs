using FCommerce.SharedKernel.Messaging;
using FCommerce.SharedKernel.Results;

namespace FCommerce.Access.Contracts;

/// <summary>
/// Creates an API key for a customer user (the host checks that the actor may manage the customer's keys; they may issue
/// one for a colleague, e.g. a buyer account made for an integration). The key acts as the owner for that customer, with
/// the rights of the owner's membership.
/// </summary>
/// <param name="ValidForDays">30, 90 or 365; null: the key does not expire.</param>
public sealed record CreateApiKey(Guid ActorId, Guid OwnerId, string CustomerCode, string Name, int? ValidForDays) : ICommand<Result<CreatedApiKey, Error>>;

/// <param name="Key">The whole key; shown once, only its hash is stored.</param>
public sealed record CreatedApiKey(Guid Id, string Key, string KeyId);

/// <summary>Revokes a key of the customer; revoking it again changes nothing.</summary>
public sealed record RevokeApiKey(Guid ActorId, string CustomerCode, Guid KeyId) : ICommand<Result<Unit, Error>>;

public sealed record GetCustomerApiKeys(string CustomerCode) : IQuery<IReadOnlyList<ApiKeySummary>>;

/// <param name="KeyId">The public identifier, the second part of "fck_{id}_{secret}".</param>
/// <param name="Status">"active", "expired" or "revoked".</param>
public sealed record ApiKeySummary(
    Guid Id,
    string Name,
    string KeyId,
    Guid OwnerId,
    string OwnerName,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? RevokedAt,
    string Status);

/// <summary>
/// Whose key it is. A wrong, revoked or expired key, or one whose user lost access, is one answer
/// ("api-key.invalid"), so keys cannot be probed. The last use is recorded (at most every few minutes).
/// </summary>
public sealed record AuthenticateApiKey(string Key) : ICommand<Result<ApiKeyIdentity, Error>>;

/// <param name="SecurityStamp">The stamp of the user's account; the session lookup checks it again.</param>
public sealed record ApiKeyIdentity(Guid KeyId, string Name, Guid UserId, string SecurityStamp, string CustomerCode);
