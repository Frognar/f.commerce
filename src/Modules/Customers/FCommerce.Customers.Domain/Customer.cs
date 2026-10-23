using FCommerce.Authorization.Core;
using FCommerce.SharedKernel.Collections;
using FCommerce.SharedKernel.Contact;
using FCommerce.SharedKernel.Imports;
using FCommerce.SharedKernel.Monetary;
using FCommerce.SharedKernel.Results;
using FCommerce.SharedKernel.Validation;

namespace FCommerce.Customers.Domain;

public sealed record Address(string Street, PostalCode PostalCode, string City, CountryCode Country);

public sealed record DeliveryAddress(string Code, string? Name, Address Address, bool IsDefault);

/// <param name="PortalAccess">The ERP grants this person access to the platform (they get an invitation).</param>
/// <param name="AttributeTemplate">Rights template for the invitation ("administrator", "buyer", …); the platform default when absent.</param>
public sealed record Contact(string ExternalId, string Name, EmailAddress? Email, string? Phone, bool PortalAccess, string? AttributeTemplate);

public sealed record Salesperson(string Name, EmailAddress? Email, string? Phone);

public sealed record CustomerContent(
    string Name,
    TaxId? TaxId,
    Address BillingAddress,
    EquatableArray<DeliveryAddress> DeliveryAddresses,
    EquatableArray<Contact> Contacts,
    Salesperson? Salesperson,
    CommercialTerms Terms);

/// <param name="PriceListCode">The customer's own price list (pricing falls back to the platform default).</param>
/// <param name="Blocked">Blocked in the ERP (e.g. overdue payments): may sign in, may not order.</param>
public sealed record CommercialTerms(string? PriceListCode, int? PaymentTermDays, Currency Currency, bool Blocked);

/// <summary>A customer (company buying from the seller) as replicated from the ERP.</summary>
public sealed record Customer(CustomerCode Code, SourceVersion Version, CustomerContent Content, bool Active);

public sealed record UnvalidatedAddress(string? Street, string? PostalCode, string? City, string? Country);

public sealed record UnvalidatedDeliveryAddress(string? Code, string? Name, UnvalidatedAddress? Address, bool IsDefault);

public sealed record UnvalidatedContact(string? ExternalId, string? Name, string? Email, string? Phone, bool PortalAccess, string? AttributeTemplate);

public sealed record UnvalidatedSalesperson(string? Name, string? Email, string? Phone);

public sealed record UnvalidatedCustomer(
    string? Code,
    long Version,
    string? Name,
    string? TaxId,
    UnvalidatedAddress? BillingAddress,
    IReadOnlyList<UnvalidatedDeliveryAddress>? DeliveryAddresses,
    IReadOnlyList<UnvalidatedContact>? Contacts,
    UnvalidatedSalesperson? Salesperson,
    string? PriceListCode,
    int? PaymentTermDays,
    string? Currency,
    bool Blocked,
    bool Active);

public static class Customers
{
    public const int MaxName = 300;
    public const int MaxStreet = 200;
    public const int MaxCity = 100;
    public const int MaxPhone = 40;
    public const int MaxAddresses = 200;
    public const int MaxContacts = 200;

    private static readonly Func<CustomerCode, SourceVersion, CustomerContent, Func<bool, Customer>> Create =
        (code, version, content) => active => new Customer(code, version, content, active);

    private static readonly Func<string, TaxId?, Address, EquatableArray<DeliveryAddress>, EquatableArray<Contact>, Salesperson?, CommercialTerms, CustomerContent> CreateContent =
        (name, taxId, billing, delivery, contacts, salesperson, terms) => new CustomerContent(name, taxId, billing, delivery, contacts, salesperson, terms);

    /// <summary>Validates a customer from the ERP, reporting every problem with its field path.</summary>
    public static Validated<Customer> Validate(UnvalidatedCustomer input) =>
        Create
            .Apply(CustomerCode.Parse(input.Code).ToValidated("code"))
            .Apply(SourceVersion.Parse(input.Version).ToValidated("version"))
            .Apply(CreateContent
                .Apply(SourceCodes.Parse(input.Name, MaxName, "name", "Name").Map(Texts.Normalize).ToValidated("name"))
                .Apply(Optional(input.TaxId, TaxId.Parse).ToValidated("taxId"))
                .Apply(ValidateAddress(input.BillingAddress).AtField("billingAddress"))
                .Apply(ValidateDeliveryAddresses(input.DeliveryAddresses))
                .Apply(ValidateContacts(input.Contacts))
                .Apply(input.Salesperson is null ? Validated.Valid<Salesperson?>(null) : ValidateSalesperson(input.Salesperson).Map(s => (Salesperson?)s).AtField("salesperson"))
                .Apply(ValidateTerms(input)))
            .Map(complete => complete(input.Active));

    public static Change<Customer> Reconcile(Customer? stored, Customer incoming) =>
        ImportBatch.Reconcile(stored, incoming, c => c.Version, (a, b) => a.Content == b.Content && a.Active == b.Active);

    /// <summary>Whether the people with platform access differ (what the access module reacts to).</summary>
    public static bool PortalContactsChanged(Customer? before, Customer after) =>
        before is null
            ? after.Content.Contacts.Any(c => c.PortalAccess)
            : !before.Content.Contacts.Where(c => c.PortalAccess).SequenceEqual(after.Content.Contacts.Where(c => c.PortalAccess));

    private static Validated<Address> ValidateAddress(UnvalidatedAddress? input) =>
        input is null
            ? Validated.Invalid<Address>(Error.Validation("address.missing", "The address is required."))
            : CountryCode.Parse(input.Country).ToValidated("country").Match(
                country => ((Func<string, PostalCode, string, Address>)((street, postal, city) => new Address(street, postal, city, country)))
                    .Apply(SourceCodes.Parse(input.Street, MaxStreet, "street", "Street").Map(Texts.Normalize).ToValidated("street"))
                    .Apply(PostalCode.Parse(input.PostalCode, country).ToValidated("postalCode"))
                    .Apply(SourceCodes.Parse(input.City, MaxCity, "city", "City").Map(Texts.Normalize).ToValidated("city")),
                errors => Validated.Invalid<Address>(errors[0], [.. errors.Skip(1)]));

    private static Validated<EquatableArray<DeliveryAddress>> ValidateDeliveryAddresses(IReadOnlyList<UnvalidatedDeliveryAddress>? input) =>
        Bounded(input, MaxAddresses, "deliveryAddresses", address =>
                ((Func<string, string?, Address, DeliveryAddress>)((code, name, value) => new DeliveryAddress(code, name, value, address.IsDefault)))
                    .Apply(SourceCodes.Parse(address.Code, 64, "address-code", "Address code").ToValidated("code"))
                    .Apply(SourceCodes.ParseOptional(address.Name, MaxName, "address-name", "Address name").ToValidated("name"))
                    .Apply(ValidateAddress(address.Address).AtField("address")))
            .EnsureAll(
                (all => all.Count(a => a.IsDefault) <= 1, Error.Validation("delivery-address.many-defaults", "At most one delivery address can be the default.") with { Field = "deliveryAddresses" }),
                (all => all.Select(a => a.Code).Distinct(StringComparer.Ordinal).Count() == all.Count, Error.Validation("delivery-address.duplicate-code", "Delivery address codes must be unique.") with { Field = "deliveryAddresses" }));

    private static Validated<EquatableArray<Contact>> ValidateContacts(IReadOnlyList<UnvalidatedContact>? input) =>
        Bounded(input, MaxContacts, "contacts", contact =>
                ((Func<string, string, EmailAddress?, string?, string?, Contact>)((id, name, email, phone, template) => new Contact(id, name, email, phone, contact.PortalAccess, template)))
                    .Apply(SourceCodes.Parse(contact.ExternalId, 64, "contact-id", "Contact id").ToValidated("externalId"))
                    .Apply(SourceCodes.Parse(contact.Name, MaxName, "contact-name", "Contact name").Map(Texts.Normalize).ToValidated("name"))
                    .Apply(Optional(contact.Email, EmailAddress.Parse).ToValidated("email"))
                    .Apply(SourceCodes.ParseOptional(contact.Phone, MaxPhone, "phone", "Phone").ToValidated("phone"))
                    .Apply((contact.AttributeTemplate is null || MembershipAttributes.Templates.ContainsKey(contact.AttributeTemplate)
                        ? Result.Ok<string?, Error>(contact.AttributeTemplate)
                        : Error.Validation("contact.template-unknown", $"Unknown attribute template '{contact.AttributeTemplate}'.")).ToValidated("attributeTemplate"))
                    .Ensure(c => !c.PortalAccess || c.Email is not null, _ => Error.Validation("contact.portal-needs-email", "A contact with platform access needs an e-mail address.") with { Field = "email" }))
            .Ensure(all => all.Select(c => c.ExternalId).Distinct(StringComparer.Ordinal).Count() == all.Count, _ => Error.Validation("contact.duplicate-id", "Contact ids must be unique.") with { Field = "contacts" });

    private static Validated<Salesperson> ValidateSalesperson(UnvalidatedSalesperson input) =>
        ((Func<string, EmailAddress?, string?, Salesperson>)((name, email, phone) => new Salesperson(name, email, phone)))
            .Apply(SourceCodes.Parse(input.Name, MaxName, "salesperson-name", "Salesperson name").Map(Texts.Normalize).ToValidated("name"))
            .Apply(Optional(input.Email, EmailAddress.Parse).ToValidated("email"))
            .Apply(SourceCodes.ParseOptional(input.Phone, MaxPhone, "phone", "Phone").ToValidated("phone"));

    private static Validated<CommercialTerms> ValidateTerms(UnvalidatedCustomer input) =>
        ((Func<string?, int?, Currency, CommercialTerms>)((priceList, terms, currency) => new CommercialTerms(priceList, terms, currency, input.Blocked)))
            .Apply(SourceCodes.ParseOptional(input.PriceListCode, 64, "price-list", "Price list").ToValidated("priceListCode"))
            .Apply((input.PaymentTermDays is null or (>= 0 and <= 365)
                ? Result.Ok<int?, Error>(input.PaymentTermDays)
                : Error.Validation("payment-terms.invalid", "Payment terms must be between 0 and 365 days.")).ToValidated("paymentTermDays"))
            .Apply(Currency.Parse(input.Currency ?? "PLN").ToValidated("currency"));

    /// <summary>Checks every rule and reports all that fail (unlike chained Ensure, which stops at the first).</summary>
    private static Validated<T> EnsureAll<T>(this Validated<T> validated, params (Func<T, bool> Holds, Error Error)[] rules) =>
        validated.Bind(value => rules.Where(rule => !rule.Holds(value)).Select(rule => rule.Error).ToList() is [var first, .. var rest]
            ? Validated.Invalid<T>(first, [.. rest])
            : Validated.Valid(value));

    private static Result<T?, Error> Optional<T>(string? raw, Func<string, Result<T, Error>> parse)
        where T : class =>
        string.IsNullOrWhiteSpace(raw) ? Result.Ok<T?, Error>(null) : parse(raw).Map(value => (T?)value);

    private static Validated<EquatableArray<TOut>> Bounded<TIn, TOut>(IReadOnlyList<TIn>? items, int max, string field, Func<TIn, Validated<TOut>> validate) =>
        (items ?? []).Count > max
            ? Validated.Invalid<EquatableArray<TOut>>(Error.Validation($"{field}.too-many", $"At most {max} items are allowed.") with { Field = field })
            : (items ?? []).Traverse(validate).Map(values => values.ToEquatableArray()).AtField(field);
}
