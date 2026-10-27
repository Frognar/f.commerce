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

    /// <summary>Only of an active account.</summary>
    public string? PasswordHash { get; set; }

    public required string SecurityStamp { get; set; }

    /// <summary>Of an active account that is not locked out.</summary>
    public int FailedSignIns { get; set; }

    /// <summary>Of an active account that was locked out.</summary>
    public DateTimeOffset? LockedUntil { get; set; }

    /// <summary>Operator attributes as JSON (<see cref="OperatorDocument"/>); null for customer users.</summary>
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
        var stored = Persisted.Dehydrate<Account, Account.Stored>(account);
        Email = stored.Email.Value;
        Name = stored.Name.Value;
        Culture = stored.Culture.Value;
        Status = stored.Status.ToString();
        PasswordHash = stored.Password.Map(hash => hash.Value).OrNull();
        SecurityStamp = stored.SecurityStamp.Value;
        FailedSignIns = stored.FailedSignIns;
        LockedUntil = stored.LockedUntil.OrNull();
        Operator = stored.Operator.Map(OperatorDocument.Write).OrNull();
    }

    public Account ToDomain() =>
        Persisted.Rehydrate<Account, Account.Stored>(new Account.Stored(
            Id,
            Persisted.Rehydrate<EmailAddress, string>(Email),
            Persisted.Rehydrate<PersonName, string>(Name),
            Persisted.Rehydrate<CultureCode, string>(Culture),
            Stored.Enum<AccountStatus>(Status, "account status"),
            PasswordHash.ToMaybe().Map(Persisted.Rehydrate<PasswordHash, string>),
            Persisted.Rehydrate<SecurityStamp, string>(SecurityStamp),
            FailedSignIns,
            LockedUntil.ToMaybe(),
            Operator.ToMaybe().Map(OperatorDocument.Read)));

    /// <summary>Stored shape of <see cref="OperatorAttributes"/>: every right must be there (a missing one is not "no").</summary>
    private sealed record OperatorDocument(bool? CanManageSettings, bool? CanManageOperators, bool? CanDecideRegistrations, CustomerScope? CustomerScope)
    {
        public static string Write(OperatorAttributes attributes) =>
            JsonSerializer.Serialize(
                new OperatorDocument(attributes.CanManageSettings, attributes.CanManageOperators, attributes.CanDecideRegistrations, attributes.CustomerScope),
                PersistenceJson.Options);

        public static OperatorAttributes Read(string json)
        {
            var document = Stored.Required(JsonSerializer.Deserialize<OperatorDocument>(json, PersistenceJson.Options), "operator attributes");
            return new OperatorAttributes(
                Stored.Required(document.CanManageSettings, "operator right to manage settings"),
                Stored.Required(document.CanManageOperators, "operator right to manage operators"),
                Stored.Required(document.CanDecideRegistrations, "operator right to decide registrations"),
                Stored.Required(document.CustomerScope, "operator customer scope"));
        }
    }
}

internal sealed class MembershipRow
{
    public Guid UserId { get; init; }

    public required string CustomerCode { get; init; }

    /// <summary>Membership attributes as JSON (<see cref="AttributesDocument"/>).</summary>
    public required string Attributes { get; set; }

    /// <summary>"Erp", "Operator", "CustomerAdmin" or "Registration".</summary>
    public required string Source { get; init; }

    /// <summary>The ERP contact of an "Erp" membership; null for every other source.</summary>
    public string? ErpContactId { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public static MembershipRow From(Membership membership, DateTimeOffset now) =>
        new()
        {
            UserId = membership.UserId,
            CustomerCode = membership.CustomerCode.Value,
            Attributes = AttributesDocument.Write(membership.Attributes),
            Source = membership.Source.Name,
            ErpContactId = membership.Source is MembershipSource.Erp erp ? erp.Contact.Value : null,
            CreatedAt = now,
        };

    /// <summary>A membership changes only its attributes; its customer and source stay.</summary>
    public void Apply(MembershipAttributes attributes) => Attributes = AttributesDocument.Write(attributes);

    public Membership ToDomain() =>
        new(UserId, Persisted.Rehydrate<Domain.CustomerCode, string>(CustomerCode), AttributesDocument.Read(Attributes), SourceOf(Source, ErpContactId));

    private static MembershipSource SourceOf(string source, string? contact) =>
        (source, contact) switch
        {
            ("Erp", { } id) => new MembershipSource.Erp(Persisted.Rehydrate<Domain.ErpContactId, string>(id)),
            ("Operator", null) => MembershipSource.Operator,
            ("CustomerAdmin", null) => MembershipSource.CustomerAdmin,
            ("Registration", null) => MembershipSource.Registration,
            _ => throw Stored.Corrupt($"membership source '{source}' with ERP contact '{contact}'"),
        };

    /// <summary>
    /// Flat storage shape of <see cref="MembershipAttributes"/>: a limit (amount with currency) and address codes only
    /// with the right to order; no codes means all addresses.
    /// </summary>
    private sealed record AttributesDocument(
        bool? CanOrder,
        OrderScope? OrderScope,
        decimal? OrderLimit,
        string? OrderLimitCurrency,
        bool? CanViewDocuments,
        bool? CanManageUsers,
        IReadOnlyList<string>? DeliveryAddressCodes)
    {
        public static string Write(MembershipAttributes attributes) =>
            JsonSerializer.Serialize(
                new AttributesDocument(
                    attributes.CanOrder,
                    attributes.OrderScope,
                    attributes.OrderLimit.Map(limit => limit.Value.Amount).OrNull(),
                    attributes.OrderLimit.Map(limit => limit.Value.Currency.Code).OrNull(),
                    attributes.CanViewDocuments,
                    attributes.CanManageUsers,
                    attributes.DeliveryAddresses is DeliveryAddressScope.OnlyAddresses only ? [.. only.Codes] : null),
                PersistenceJson.Options);

        public static MembershipAttributes Read(string json)
        {
            var document = Stored.Required(JsonSerializer.Deserialize<AttributesDocument>(json, PersistenceJson.Options), "membership attributes");
            var ordering = (Stored.Required(document.CanOrder, "right to order"), document.OrderLimit, document.OrderLimitCurrency, document.DeliveryAddressCodes) switch
            {
                (false, null, null, null) => OrderingRight.NotAllowed,
                (true, var amount, var currency, var codes) => new OrderingRight.Allowed(Limit(amount, currency), Addresses(codes)),
                _ => throw Stored.Corrupt("a membership without the right to order has a limit or delivery addresses"),
            };
            return new MembershipAttributes(
                ordering,
                Stored.Required(document.OrderScope, "order scope"),
                Stored.Required(document.CanViewDocuments, "right to view documents"),
                Stored.Required(document.CanManageUsers, "right to manage users"));
        }

        private static Maybe<OrderLimit> Limit(decimal? amount, string? currency) =>
            (amount, currency) switch
            {
                (null, null) => Maybe<OrderLimit>.None,
                ({ } value, { } code) => Persisted.Rehydrate<OrderLimit, Money>(new Money(value, Persisted.Rehydrate<Currency, string>(code))),
                _ => throw Stored.Corrupt("an order limit without its amount or currency"),
            };

        private static DeliveryAddressScope Addresses(IReadOnlyList<string>? codes) =>
            codes switch
            {
                null => DeliveryAddressScope.All,
                [] => throw Stored.Corrupt("an empty list of allowed delivery addresses"),
                _ => DeliveryAddressScope.Of(codes),
            };
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

    /// <summary>Of a decided request.</summary>
    public DateTimeOffset? DecidedAt { get; set; }

    /// <summary>Of a decided request.</summary>
    public Guid? DecidedBy { get; set; }

    /// <summary>Of an approved request.</summary>
    public string? CustomerCode { get; set; }

    /// <summary>Of a rejected request.</summary>
    public string? RejectionReason { get; set; }

    public static RegistrationRow From(RegistrationRequest request)
    {
        var row = new RegistrationRow
        {
            Id = request.Id,
            Status = string.Empty,
            CompanyName = request.Company.Name.Value,
            TaxId = request.Company.TaxId.Value,
            Email = request.Applicant.Email.Value,
            Form = JsonSerializer.Serialize(new FormDocument(request.Company, request.Applicant), PersistenceJson.Options),
            SubmittedAt = request.SubmittedAt,
        };
        row.Apply(request);
        return row;
    }

    public void Apply(RegistrationRequest request) =>
        (Status, DecidedAt, DecidedBy, CustomerCode, RejectionReason) = request.Status switch
        {
            RegistrationStatus.Approved a => ("Approved", (DateTimeOffset?)a.At, (Guid?)a.DecidedBy, a.CustomerCode.Value, (string?)null),
            RegistrationStatus.Rejected r => ("Rejected", r.At, r.DecidedBy, null, r.Reason.Value),
            RegistrationStatus.PendingDecision => ("Pending", null, null, null, null),
            _ => throw new ArgumentOutOfRangeException(nameof(request), request.Status, "A registration status without a stored form."),
        };

    public RegistrationRequest ToDomain()
    {
        var form = Stored.Required(JsonSerializer.Deserialize<FormDocument>(Form, PersistenceJson.Options), "registration form");
        RegistrationStatus status = (Status, DecidedAt, DecidedBy, CustomerCode, RejectionReason) switch
        {
            ("Pending", null, null, null, null) => RegistrationStatus.Pending,
            ("Approved", { } at, { } by, { } customer, null) => new RegistrationStatus.Approved(by, at, Persisted.Rehydrate<Domain.CustomerCode, string>(customer)),
            ("Rejected", { } at, { } by, null, { } reason) => new RegistrationStatus.Rejected(by, at, Persisted.Rehydrate<FormText, string>(reason)),
            _ => throw Stored.Corrupt($"registration {Id} with status '{Status}' and decision fields that do not fit it"),
        };
        return Persisted.Rehydrate<RegistrationRequest, RegistrationRequest.Stored>(new RegistrationRequest.Stored(Id, form.Company, form.Applicant, SubmittedAt, status));
    }

    private sealed record FormDocument(ApplicantCompany Company, Applicant Applicant);
}

internal sealed class TokenRow
{
    public long Id { get; init; }

    public Guid AccountId { get; init; }

    public required string Purpose { get; init; }

    public required string Hash { get; init; }

    public DateTimeOffset IssuedAt { get; init; }

    public DateTimeOffset? UsedAt { get; set; }

    public OneTimeToken ToDomain() =>
        Persisted.Rehydrate<OneTimeToken, OneTimeToken.Stored>(new OneTimeToken.Stored(
            AccountId, Stored.Enum<TokenPurpose>(Purpose, "token purpose"), Persisted.Rehydrate<SecretHash, string>(Hash), IssuedAt, UsedAt.ToMaybe()));

    public void Apply(OneTimeToken token) => UsedAt = token.UsedAt.OrNull();

    public static TokenRow From(OneTimeToken token) =>
        new() { AccountId = token.AccountId, Purpose = token.Purpose.ToString(), Hash = token.Hash.Value, IssuedAt = token.IssuedAt, UsedAt = token.UsedAt.OrNull() };
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
            CustomerCode = key.CustomerCode.Value,
            Name = key.Name.Value,
            KeyId = key.KeyId.Value,
            SecretHash = key.SecretHash.Value,
            SecurityStamp = key.SecurityStamp.Value,
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
        Persisted.Rehydrate<ApiKey, ApiKey.Stored>(new ApiKey.Stored(
            Id,
            UserId,
            Persisted.Rehydrate<Domain.CustomerCode, string>(CustomerCode),
            Persisted.Rehydrate<ApiKeyName, string>(Name),
            Persisted.Rehydrate<ApiKeyId, string>(KeyId),
            Persisted.Rehydrate<Domain.SecretHash, string>(SecretHash),
            Persisted.Rehydrate<Domain.SecurityStamp, string>(SecurityStamp),
            CreatedAt,
            ExpiresAt.ToMaybe(),
            LastUsedAt.ToMaybe(),
            RevokedAt.ToMaybe()));
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
