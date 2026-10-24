using FCommerce.Authorization.Core;
using FCommerce.SharedKernel.Collections;
using FCommerce.SharedKernel.Contact;
using FCommerce.SharedKernel.Imports;
using FCommerce.SharedKernel.Monetary;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Results;
using FCommerce.SharedKernel.Validation;

namespace FCommerce.Customers.Domain;

public sealed record Address(string Street, PostalCode PostalCode, string City, CountryCode Country);

public sealed record DeliveryAddress(string Code, Maybe<string> Name, Address Address, bool IsDefault);

/// <param name="PortalAccess">The ERP grants this person access to the platform (they get an invitation).</param>
/// <param name="AttributeTemplate">Rights template for the invitation ("administrator", "buyer", …); the platform default when absent.</param>
public sealed record Contact(string ExternalId, string Name, Maybe<EmailAddress> Email, Maybe<string> Phone, bool PortalAccess, Maybe<string> AttributeTemplate);

public sealed record Salesperson(string Name, Maybe<EmailAddress> Email, Maybe<string> Phone);

public sealed record CustomerContent(
    string Name,
    Maybe<TaxId> TaxId,
    Address BillingAddress,
    EquatableArray<DeliveryAddress> DeliveryAddresses,
    EquatableArray<Contact> Contacts,
    Maybe<Salesperson> Salesperson,
    CommercialTerms Terms);

/// <param name="PriceListCode">The customer's own price list (pricing falls back to the platform default).</param>
/// <param name="Blocked">Blocked in the ERP (e.g. overdue payments): may sign in, may not order.</param>
public sealed record CommercialTerms(Maybe<string> PriceListCode, Maybe<int> PaymentTermDays, Currency Currency, bool Blocked);

/// <summary>A customer (company buying from the seller) as replicated from the ERP.</summary>
public sealed record Customer(CustomerCode Code, SourceVersion Version, CustomerContent Content, bool Active);

public sealed record UnvalidatedAddress(string Street, string PostalCode, string City, string Country);

public sealed record UnvalidatedDeliveryAddress(string Code, Maybe<string> Name, Maybe<UnvalidatedAddress> Address, bool IsDefault);

public sealed record UnvalidatedContact(string ExternalId, string Name, Maybe<string> Email, Maybe<string> Phone, bool PortalAccess, Maybe<string> AttributeTemplate);

public sealed record UnvalidatedSalesperson(string Name, Maybe<string> Email, Maybe<string> Phone);

/// <summary>A customer as received from the ERP; missing required texts arrive as empty, optional parts are absent.</summary>
public sealed record UnvalidatedCustomer(
    string Code,
    long Version,
    string Name,
    Maybe<string> TaxId,
    Maybe<UnvalidatedAddress> BillingAddress,
    IReadOnlyList<UnvalidatedDeliveryAddress> DeliveryAddresses,
    IReadOnlyList<UnvalidatedContact> Contacts,
    Maybe<UnvalidatedSalesperson> Salesperson,
    Maybe<string> PriceListCode,
    Maybe<int> PaymentTermDays,
    Maybe<string> Currency,
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

    private static readonly Func<string, Maybe<TaxId>, Address, EquatableArray<DeliveryAddress>, EquatableArray<Contact>, Maybe<Salesperson>, CommercialTerms, CustomerContent> CreateContent =
        (name, taxId, billing, delivery, contacts, salesperson, terms) => new CustomerContent(name, taxId, billing, delivery, contacts, salesperson, terms);

    /// <summary>Validates a customer from the ERP, reporting every problem with its field path.</summary>
    public static Validated<Customer> Validate(UnvalidatedCustomer input) =>
        Create
            .Apply(CustomerCode.Parse(input.Code).ToValidated("code"))
            .Apply(SourceVersion.Parse(input.Version).ToValidated("version"))
            .Apply(CreateContent
                .Apply(SourceCodes.Parse(input.Name, MaxName, "name", "Name").Map(Texts.Normalize).ToValidated("name"))
                .Apply(Maybe.ParseOptional(input.TaxId, TaxId.Parse).ToValidated("taxId"))
                .Apply(ValidateAddress(input.BillingAddress).AtField("billingAddress"))
                .Apply(ValidateDeliveryAddresses(input.DeliveryAddresses))
                .Apply(ValidateContacts(input.Contacts))
                .Apply(input.Salesperson.Match(salesperson => ValidateSalesperson(salesperson).Map(Maybe.Some).AtField("salesperson"), () => Validated.Valid(Maybe<Salesperson>.None)))
                .Apply(ValidateTerms(input)))
            .Map(complete => complete(input.Active));

    public static Change<Customer> Reconcile(Maybe<Customer> stored, Customer incoming) =>
        ImportBatch.Reconcile(stored, incoming, c => c.Version, (a, b) => a.Content == b.Content && a.Active == b.Active);

    /// <summary>Whether the people with platform access differ (what the access module reacts to).</summary>
    public static bool PortalContactsChanged(Maybe<Customer> before, Customer after) =>
        before.Match(
            previous => !previous.Content.Contacts.Where(c => c.PortalAccess).SequenceEqual(after.Content.Contacts.Where(c => c.PortalAccess)),
            () => after.Content.Contacts.Any(c => c.PortalAccess));

    private static Validated<Address> ValidateAddress(Maybe<UnvalidatedAddress> input) =>
        input.Match(
            address => CountryCode.Parse(address.Country).ToValidated("country").Match(
                country => ((Func<string, PostalCode, string, Address>)((street, postal, city) => new Address(street, postal, city, country)))
                    .Apply(SourceCodes.Parse(address.Street, MaxStreet, "street", "Street").Map(Texts.Normalize).ToValidated("street"))
                    .Apply(PostalCode.Parse(address.PostalCode, country).ToValidated("postalCode"))
                    .Apply(SourceCodes.Parse(address.City, MaxCity, "city", "City").Map(Texts.Normalize).ToValidated("city")),
                errors => Validated.Invalid<Address>(errors[0], [.. errors.Skip(1)])),
            () => Validated.Invalid<Address>(Error.Validation("address.missing", "The address is required.")));

    private static Validated<EquatableArray<DeliveryAddress>> ValidateDeliveryAddresses(IReadOnlyList<UnvalidatedDeliveryAddress> input) =>
        Bounded(input, MaxAddresses, "deliveryAddresses", address =>
                ((Func<string, Maybe<string>, Address, DeliveryAddress>)((code, name, value) => new DeliveryAddress(code, name, value, address.IsDefault)))
                    .Apply(SourceCodes.Parse(address.Code, 64, "address-code", "Address code").ToValidated("code"))
                    .Apply(SourceCodes.ParseOptional(address.Name, MaxName, "address-name", "Address name").ToValidated("name"))
                    .Apply(ValidateAddress(address.Address).AtField("address")))
            .EnsureAll(
                (all => all.Count(a => a.IsDefault) <= 1, Error.Validation("delivery-address.many-defaults", "At most one delivery address can be the default.") with { Field = "deliveryAddresses" }),
                (all => all.Select(a => a.Code).Distinct(StringComparer.Ordinal).Count() == all.Count, Error.Validation("delivery-address.duplicate-code", "Delivery address codes must be unique.") with { Field = "deliveryAddresses" }));

    private static Validated<EquatableArray<Contact>> ValidateContacts(IReadOnlyList<UnvalidatedContact> input) =>
        Bounded(input, MaxContacts, "contacts", contact =>
                ((Func<string, string, Maybe<EmailAddress>, Maybe<string>, Maybe<string>, Contact>)((id, name, email, phone, template) => new Contact(id, name, email, phone, contact.PortalAccess, template)))
                    .Apply(SourceCodes.Parse(contact.ExternalId, 64, "contact-id", "Contact id").ToValidated("externalId"))
                    .Apply(SourceCodes.Parse(contact.Name, MaxName, "contact-name", "Contact name").Map(Texts.Normalize).ToValidated("name"))
                    .Apply(Maybe.ParseOptional(contact.Email, EmailAddress.Parse).ToValidated("email"))
                    .Apply(SourceCodes.ParseOptional(contact.Phone, MaxPhone, "phone", "Phone").ToValidated("phone"))
                    .Apply(contact.AttributeTemplate.Match(
                        template => MembershipAttributes.Templates.ContainsKey(template)
                            ? Result.Ok<Maybe<string>, Error>(template)
                            : Error.Validation("contact.template-unknown", $"Unknown attribute template '{template}'."),
                        () => Result.Ok<Maybe<string>, Error>(Maybe<string>.None)).ToValidated("attributeTemplate"))
                    .Ensure(c => !c.PortalAccess || c.Email.Match(_ => true, () => false), _ => Error.Validation("contact.portal-needs-email", "A contact with platform access needs an e-mail address.") with { Field = "email" }))
            .Ensure(all => all.Select(c => c.ExternalId).Distinct(StringComparer.Ordinal).Count() == all.Count, _ => Error.Validation("contact.duplicate-id", "Contact ids must be unique.") with { Field = "contacts" });

    private static Validated<Salesperson> ValidateSalesperson(UnvalidatedSalesperson input) =>
        ((Func<string, Maybe<EmailAddress>, Maybe<string>, Salesperson>)((name, email, phone) => new Salesperson(name, email, phone)))
            .Apply(SourceCodes.Parse(input.Name, MaxName, "salesperson-name", "Salesperson name").Map(Texts.Normalize).ToValidated("name"))
            .Apply(Maybe.ParseOptional(input.Email, EmailAddress.Parse).ToValidated("email"))
            .Apply(SourceCodes.ParseOptional(input.Phone, MaxPhone, "phone", "Phone").ToValidated("phone"));

    private static Validated<CommercialTerms> ValidateTerms(UnvalidatedCustomer input) =>
        ((Func<Maybe<string>, Maybe<int>, Currency, CommercialTerms>)((priceList, terms, currency) => new CommercialTerms(priceList, terms, currency, input.Blocked)))
            .Apply(SourceCodes.ParseOptional(input.PriceListCode, 64, "price-list", "Price list").ToValidated("priceListCode"))
            .Apply((input.PaymentTermDays.Exists(days => days is < 0 or > 365)
                ? Error.Validation("payment-terms.invalid", "Payment terms must be between 0 and 365 days.")
                : Result.Ok<Maybe<int>, Error>(input.PaymentTermDays)).ToValidated("paymentTermDays"))
            .Apply(Currency.Parse(input.Currency.ValueOr("PLN")).ToValidated("currency"));

    private static Validated<EquatableArray<TOut>> Bounded<TIn, TOut>(IReadOnlyList<TIn> items, int max, string field, Func<TIn, Validated<TOut>> validate)
        where TIn : notnull
        where TOut : notnull =>
        items.Count > max
            ? Validated.Invalid<EquatableArray<TOut>>(Error.Validation($"{field}.too-many", $"At most {max} items are allowed.") with { Field = field })
            : items.Traverse(validate).Map(values => values.ToEquatableArray()).AtField(field);
}
