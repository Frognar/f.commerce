using System.Text.Json;
using FCommerce.Access.Domain;
using FCommerce.Authorization.Core;
using FCommerce.Infrastructure.Serialization;
using FCommerce.SharedKernel.Contact;
using FCommerce.SharedKernel.Localization;
using FCommerce.SharedKernel.Monetary;

namespace FCommerce.Access.Persistence;

internal sealed class AccountRow
{
    public Guid Id { get; init; }

    public required string Email { get; set; }

    public required string Name { get; set; }

    public required string Culture { get; set; }

    public required string Status { get; set; }

    public string? PasswordHash { get; set; }

    public required string SecurityStamp { get; set; }

    public int FailedSignIns { get; set; }

    public DateTimeOffset? LockedUntil { get; set; }

    /// <summary>Operator attributes as JSON; null for customer users.</summary>
    public string? Operator { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public static AccountRow From(Account account, DateTimeOffset now)
    {
        var row = new AccountRow { Id = account.Id, Email = string.Empty, Name = string.Empty, Culture = string.Empty, Status = string.Empty, SecurityStamp = string.Empty, CreatedAt = now };
        row.Apply(account);
        return row;
    }

    public void Apply(Account account)
    {
        Email = account.Email.Value;
        Name = account.Name.Value;
        Culture = account.Culture.Value;
        Status = account.Status.ToString();
        PasswordHash = account.PasswordHash;
        SecurityStamp = account.SecurityStamp;
        FailedSignIns = account.FailedSignIns;
        LockedUntil = account.LockedUntil;
        Operator = account.Operator is null ? null : JsonSerializer.Serialize(account.Operator, PersistenceJson.Options);
    }

    public Account ToDomain() =>
        new(
            Id,
            Persisted.Rehydrate<EmailAddress, string>(Email),
            Persisted.Rehydrate<PersonName, string>(Name),
            Persisted.Rehydrate<CultureCode, string>(Culture),
            Enum.Parse<AccountStatus>(Status),
            PasswordHash,
            SecurityStamp,
            FailedSignIns,
            LockedUntil,
            Operator is null ? null : JsonSerializer.Deserialize<OperatorAttributes>(Operator, PersistenceJson.Options));
}

internal sealed class MembershipRow
{
    public Guid UserId { get; init; }

    public required string CustomerCode { get; init; }

    /// <summary>Membership attributes as JSON (<see cref="AttributesDocument"/>).</summary>
    public required string Attributes { get; set; }

    public required string Source { get; init; }

    public string? ErpContactId { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public static MembershipRow From(Membership membership, DateTimeOffset now) =>
        new()
        {
            UserId = membership.UserId,
            CustomerCode = membership.CustomerCode,
            Attributes = AttributesDocument.Write(membership.Attributes),
            Source = membership.Source.ToString(),
            ErpContactId = membership.ErpContactId,
            CreatedAt = now,
        };

    public void Apply(Membership membership)
    {
        Attributes = AttributesDocument.Write(membership.Attributes);
        ErpContactId = membership.ErpContactId;
    }

    public Membership ToDomain() =>
        new(UserId, CustomerCode, AttributesDocument.Read(Attributes), Enum.Parse<MembershipSource>(Source), ErpContactId);

    /// <summary>Flat storage shape of <see cref="MembershipAttributes"/> (its address scope is a closed hierarchy).</summary>
    private sealed record AttributesDocument(
        bool CanOrder,
        OrderScope OrderScope,
        decimal? OrderLimit,
        string? OrderLimitCurrency,
        bool CanViewDocuments,
        bool CanManageUsers,
        IReadOnlyList<string>? DeliveryAddressCodes)
    {
        public static string Write(MembershipAttributes attributes) =>
            JsonSerializer.Serialize(
                new AttributesDocument(
                    attributes.CanOrder,
                    attributes.OrderScope,
                    attributes.OrderLimit?.Amount,
                    attributes.OrderLimit?.Currency.Code,
                    attributes.CanViewDocuments,
                    attributes.CanManageUsers,
                    attributes.DeliveryAddresses is DeliveryAddressScope.OnlyAddresses only ? [.. only.Codes] : null),
                PersistenceJson.Options);

        public static MembershipAttributes Read(string json)
        {
            var document = JsonSerializer.Deserialize<AttributesDocument>(json, PersistenceJson.Options) ?? throw new JsonException("Stored membership attributes are null.");
            return new MembershipAttributes(
                document.CanOrder,
                document.OrderScope,
                document is { OrderLimit: { } amount, OrderLimitCurrency: { } currency } ? new Money(amount, Persisted.Rehydrate<Currency, string>(currency)) : null,
                document.CanViewDocuments,
                document.CanManageUsers,
                document.DeliveryAddressCodes is { } codes ? DeliveryAddressScope.Only([.. codes]) : DeliveryAddressScope.All);
        }
    }
}

internal sealed class TokenRow
{
    public long Id { get; init; }

    public Guid AccountId { get; init; }

    public required string Purpose { get; init; }

    public required string Hash { get; init; }

    public DateTimeOffset ExpiresAt { get; init; }

    public DateTimeOffset? UsedAt { get; set; }

    public OneTimeToken ToDomain() => new(AccountId, Enum.Parse<TokenPurpose>(Purpose), Hash, ExpiresAt, UsedAt);

    public static TokenRow From(OneTimeToken token) =>
        new() { AccountId = token.AccountId, Purpose = token.Purpose.ToString(), Hash = token.Hash, ExpiresAt = token.ExpiresAt, UsedAt = token.UsedAt };
}

/// <summary>Append-only record of security-relevant changes, written in the same transaction as the change.</summary>
internal sealed class AuditEntry
{
    public long Id { get; init; }

    public DateTimeOffset At { get; init; }

    /// <summary>Who did it; null for the system (bootstrap, ERP-driven changes).</summary>
    public Guid? ActorId { get; init; }

    public required string Action { get; init; }

    /// <summary>What it concerns, e.g. "account:{id}" or "customer:{code}".</summary>
    public required string Subject { get; init; }

    public string? Details { get; init; }
}
