using System.Text.Json;
using FCommerce.Access.Domain;
using FCommerce.Authorization.Core;
using FCommerce.Infrastructure.Persistence;
using FCommerce.Infrastructure.Serialization;
using FCommerce.SharedKernel.Contact;
using FCommerce.SharedKernel.Localization;
using FCommerce.SharedKernel.Monetary;
using FCommerce.SharedKernel.Optional;

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
        PasswordHash = account.PasswordHash.OrNull();
        SecurityStamp = account.SecurityStamp;
        FailedSignIns = account.FailedSignIns;
        LockedUntil = account.LockedUntil.OrNull();
        Operator = account.Operator.Map(attributes => JsonSerializer.Serialize(attributes, PersistenceJson.Options)).OrNull();
    }

    public Account ToDomain() =>
        new(
            Id,
            Persisted.Rehydrate<EmailAddress, string>(Email),
            Persisted.Rehydrate<PersonName, string>(Name),
            Persisted.Rehydrate<CultureCode, string>(Culture),
            Stored.Enum<AccountStatus>(Status, "account status"),
            PasswordHash.ToMaybe(),
            SecurityStamp,
            FailedSignIns,
            LockedUntil.ToMaybe(),
            Operator.ToMaybe().Bind(json => JsonSerializer.Deserialize<OperatorAttributes>(json, PersistenceJson.Options).ToMaybe()));
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
            ErpContactId = membership.ErpContactId.OrNull(),
            CreatedAt = now,
        };

    public void Apply(Membership membership)
    {
        Attributes = AttributesDocument.Write(membership.Attributes);
        ErpContactId = membership.ErpContactId.OrNull();
    }

    public Membership ToDomain() =>
        new(UserId, CustomerCode, AttributesDocument.Read(Attributes), Stored.Enum<MembershipSource>(Source, "membership source"), ErpContactId.ToMaybe());

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
                    attributes.OrderLimit.Map(limit => limit.Amount).OrNull(),
                    attributes.OrderLimit.Map(limit => limit.Currency.Code).OrNull(),
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
                document is { OrderLimit: { } amount, OrderLimitCurrency: { } currency } ? new Money(amount, Persisted.Rehydrate<Currency, string>(currency)) : Maybe<Money>.None,
                document.CanViewDocuments,
                document.CanManageUsers,
                document.DeliveryAddressCodes is { } codes ? DeliveryAddressScope.Only([.. codes]) : DeliveryAddressScope.All);
        }
    }
}

internal sealed class RegistrationRow
{
    public Guid Id { get; init; }

    /// <summary>"Pending", "Approved" or "Rejected".</summary>
    public required string Status { get; set; }

    public required string CompanyName { get; init; }

    public required string TaxId { get; init; }

    public required string Email { get; init; }

    /// <summary>The submitted form as JSON (<see cref="FormDocument"/>).</summary>
    public required string Form { get; init; }

    public DateTimeOffset SubmittedAt { get; init; }

    public DateTimeOffset? DecidedAt { get; set; }

    public Guid? DecidedBy { get; set; }

    public string? CustomerCode { get; set; }

    public string? RejectionReason { get; set; }

    public static RegistrationRow From(RegistrationRequest request)
    {
        var row = new RegistrationRow
        {
            Id = request.Id,
            Status = string.Empty,
            CompanyName = request.Company.Name,
            TaxId = request.Company.TaxId.Value,
            Email = request.Applicant.Email.Value,
            Form = JsonSerializer.Serialize(FormDocument.Of(request), PersistenceJson.Options),
            SubmittedAt = request.SubmittedAt,
        };
        row.Apply(request);
        return row;
    }

    public void Apply(RegistrationRequest request)
    {
        (Status, DecidedAt, DecidedBy, CustomerCode, RejectionReason) = request.Status switch
        {
            RegistrationStatus.Approved a => ("Approved", (DateTimeOffset?)a.At, (Guid?)a.DecidedBy, a.CustomerCode, (string?)null),
            RegistrationStatus.Rejected r => ("Rejected", r.At, r.DecidedBy, null, r.Reason),
            _ => ("Pending", null, null, null, null),
        };
    }

    public RegistrationRequest ToDomain()
    {
        var form = JsonSerializer.Deserialize<FormDocument>(Form, PersistenceJson.Options) ?? throw new JsonException("Stored registration form is null.");
        RegistrationStatus status = Status switch
        {
            "Approved" => new RegistrationStatus.Approved(DecidedBy!.Value, DecidedAt!.Value, CustomerCode!),
            "Rejected" => new RegistrationStatus.Rejected(DecidedBy!.Value, DecidedAt!.Value, RejectionReason!),
            _ => RegistrationStatus.Pending,
        };
        return new RegistrationRequest(Id, form.Company, form.Applicant, SubmittedAt, status);
    }

    private sealed record FormDocument(ApplicantCompany Company, Applicant Applicant)
    {
        public static FormDocument Of(RegistrationRequest request) => new(request.Company, request.Applicant);
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

    public OneTimeToken ToDomain() => new(AccountId, Stored.Enum<TokenPurpose>(Purpose, "token purpose"), Hash, ExpiresAt, UsedAt.ToMaybe());

    public static TokenRow From(OneTimeToken token) =>
        new() { AccountId = token.AccountId, Purpose = token.Purpose.ToString(), Hash = token.Hash, ExpiresAt = token.ExpiresAt, UsedAt = token.UsedAt.OrNull() };
}

internal sealed class ApiKeyRow
{
    public Guid Id { get; init; }

    public Guid UserId { get; init; }

    public required string CustomerCode { get; init; }

    public required string Name { get; init; }

    public required string KeyId { get; init; }

    public required string SecretHash { get; init; }

    public required string SecurityStamp { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    public DateTimeOffset? LastUsedAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    public static ApiKeyRow From(ApiKey key) =>
        new()
        {
            Id = key.Id,
            UserId = key.UserId,
            CustomerCode = key.CustomerCode,
            Name = key.Name,
            KeyId = key.KeyId,
            SecretHash = key.SecretHash,
            SecurityStamp = key.SecurityStamp,
            CreatedAt = key.CreatedAt,
            ExpiresAt = key.ExpiresAt.OrNull(),
            LastUsedAt = key.LastUsedAt.OrNull(),
            RevokedAt = key.RevokedAt.OrNull(),
        };

    public void Apply(ApiKey key)
    {
        LastUsedAt = key.LastUsedAt.OrNull();
        RevokedAt = key.RevokedAt.OrNull();
    }

    public ApiKey ToDomain() =>
        new(Id, UserId, CustomerCode, Name, KeyId, SecretHash, SecurityStamp, CreatedAt, ExpiresAt.ToMaybe(), LastUsedAt.ToMaybe(), RevokedAt.ToMaybe());
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
