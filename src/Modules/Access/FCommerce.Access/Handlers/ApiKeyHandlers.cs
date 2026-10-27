using FCommerce.Access.Contracts;
using FCommerce.Access.Domain;
using FCommerce.Access.Persistence;
using FCommerce.Access.Security;
using FCommerce.Application.Messaging;
using FCommerce.Application.Results;
using FCommerce.Infrastructure.Persistence;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace FCommerce.Access.Handlers;

internal sealed class CreateApiKeyHandler(AccessStore store) : ICommandHandler<CreateApiKey, Result<CreatedApiKey, Error>>
{
    public async Task<Result<CreatedApiKey, Error>> HandleAsync(CreateApiKey command, CancellationToken cancellationToken = default)
    {
        var owner = (await store.FindAsync(command.OwnerId, cancellationToken)).Map(row => row.ToDomain());
        var hasCustomer = await store.Db.Memberships.AnyAsync(m => m.UserId == command.OwnerId && m.CustomerCode == command.CustomerCode, cancellationToken);

        return await owner.Where(_ => hasCustomer).Match(
            account => CreateAsync(account, command, cancellationToken),
            () => Task.FromResult(Result.Fail<CreatedApiKey, Error>(Error.Forbidden("api-key.no-access", "The user has no access to this customer.") with { Field = "ownerId" })));
    }

    private Task<Result<CreatedApiKey, Error>> CreateAsync(Account owner, CreateApiKey command, CancellationToken cancellationToken)
    {
        var (keyId, secret, hash) = ApiKeySecrets.New();
        return (from customer in CustomerCode.Parse(command.CustomerCode.OrEmpty())
                from name in ApiKeyName.Parse(command.Name.OrEmpty())
                from validity in ApiKey.Validity(command.ValidForDays.ToMaybe())
                from key in ApiKey.Issue(Guid.CreateVersion7(), owner, customer, name, validity, keyId, hash, store.Now)
                select key)
            .MapAsync(async key =>
            {
                store.Db.ApiKeys.Add(ApiKeyRow.From(key));
                store.Audit(command.ActorId, "api-key.created", MembershipManagement.Subject(command.CustomerCode), new { keyId = key.KeyId.Value, name = key.Name.Value, email = owner.Email.Value, expiresAt = key.ExpiresAt.OrNull() });
                await store.Db.SaveChangesAsync(cancellationToken);
                return new CreatedApiKey(key.Id, ApiKeyFormat.Compose(keyId, secret), keyId.Value);
            });
    }
}

internal sealed class RevokeApiKeyHandler(AccessStore store) : ICommandHandler<RevokeApiKey, Result<Unit, Error>>
{
    public async Task<Result<Unit, Error>> HandleAsync(RevokeApiKey command, CancellationToken cancellationToken = default) =>
        await (await store.Db.ApiKeys.SingleOrNoneAsync(k => k.Id == command.KeyId && k.CustomerCode == command.CustomerCode, cancellationToken)).Match(
            async row =>
            {
                var key = row.ToDomain();
                var revoked = key.Revoke(store.Now);
                if (revoked != key)
                {
                    row.Apply(revoked);
                    store.Audit(command.ActorId, "api-key.revoked", MembershipManagement.Subject(command.CustomerCode), new { keyId = key.KeyId.Value, name = key.Name.Value });
                    await store.Db.SaveChangesAsync(cancellationToken);
                }

                return Result.Ok<Unit, Error>(Unit.Value);
            },
            () => Task.FromResult(Result.Fail<Unit, Error>(Error.NotFound("api-key.not-found", "The API key does not exist."))));
}

internal sealed class GetCustomerApiKeysHandler(AccessDbContext db, TimeProvider time) : IQueryHandler<GetCustomerApiKeys, IReadOnlyList<ApiKeySummary>>
{
    public async Task<IReadOnlyList<ApiKeySummary>> HandleAsync(GetCustomerApiKeys query, CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow();
        var keys = await db.ApiKeys.AsNoTracking()
            .Where(k => k.CustomerCode == query.CustomerCode)
            .Join(db.Accounts.AsNoTracking(), k => k.UserId, a => a.Id, (key, account) => new { key, account.Name })
            .OrderByDescending(x => x.key.CreatedAt)
            .ToListAsync(cancellationToken);

        return [.. keys.Select(x =>
        {
            var key = x.key.ToDomain();
            return new ApiKeySummary(
                key.Id,
                key.Name.Value,
                key.KeyId.Value,
                key.UserId,
                x.Name,
                key.CreatedAt,
                key.LastUsedAt.OrNull(),
                key.ExpiresAt.OrNull(),
                key.RevokedAt.OrNull(),
                key.IsRevoked ? "revoked" : key.IsExpired(now) ? "expired" : "active");
        })];
    }
}

internal sealed class AuthenticateApiKeyHandler(AccessStore store) : ICommandHandler<AuthenticateApiKey, Result<ApiKeyIdentity, Error>>
{
    public static Error Invalid() => Error.Forbidden("api-key.invalid", "The API key is not valid.");

    public async Task<Result<ApiKeyIdentity, Error>> HandleAsync(AuthenticateApiKey command, CancellationToken cancellationToken = default) =>
        await ApiKeyFormat.Parse(command.Key.OrEmpty()).Match(
            parts => AuthenticateAsync(parts, cancellationToken),
            _ => Task.FromResult(Result.Fail<ApiKeyIdentity, Error>(Invalid())));

    private async Task<Result<ApiKeyIdentity, Error>> AuthenticateAsync(ApiKeyParts parts, CancellationToken cancellationToken)
    {
        var row = await store.Db.ApiKeys.SingleOrNoneAsync(k => k.KeyId == parts.Id.Value, cancellationToken);

        // The hash is compared in any case, so an unknown identifier takes as long as a wrong secret.
        var secretMatches = ApiKeySecrets.Matches(row.Map(r => r.SecretHash).ValueOr(ApiKeySecrets.Decoy), parts.Secret) && row.Exists(_ => true);

        return await row.Where(_ => secretMatches).Match(
            async found =>
            {
                var key = found.ToDomain();
                var owner = (await store.FindAsync(key.UserId, cancellationToken)).Map(a => a.ToDomain());
                var hasCustomer = await store.Db.Memberships.AnyAsync(m => m.UserId == key.UserId && m.CustomerCode == key.CustomerCode.Value, cancellationToken);
                var checkedKey = owner.Match(
                    account => key.Check(secretMatches, account, hasCustomer, store.Now).Match(_ => Maybe.Some(account), _ => Maybe<Account>.None),
                    () => Maybe<Account>.None);

                return await checkedKey.Match(
                    async account =>
                    {
                        if (key.ShouldRecordUse(store.Now))
                        {
                            found.Apply(key.RecordUse(store.Now));
                            await store.Db.SaveChangesAsync(cancellationToken);
                        }

                        return Result.Ok<ApiKeyIdentity, Error>(new ApiKeyIdentity(key.Id, key.Name.Value, account.Id, account.SecurityStamp.Value, key.CustomerCode.Value));
                    },
                    () => Task.FromResult(Result.Fail<ApiKeyIdentity, Error>(Invalid())));
            },
            () => Task.FromResult(Result.Fail<ApiKeyIdentity, Error>(Invalid())));
    }
}
