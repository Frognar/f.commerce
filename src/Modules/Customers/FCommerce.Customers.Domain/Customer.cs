using FCommerce.Authorization.Core;
using FCommerce.SharedKernel.Collections;
using FCommerce.SharedKernel.Contact;
using FCommerce.SharedKernel.Imports;
using FCommerce.SharedKernel.Monetary;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Persistence;
using FCommerce.SharedKernel.Results;
using FCommerce.SharedKernel.Validation;

namespace FCommerce.Customers.Domain;

public sealed record Address(CustomerText Street, PostalCode PostalCode, CustomerText City, CountryCode Country);

public sealed record DeliveryAddress(CustomerText Code, Maybe<CustomerText> Name, Address Address);

/// <summary>The delivery addresses of a customer: unique codes, at most one of them the default.</summary>
public sealed record DeliveryAddressBook : IPersistedValue<DeliveryAddressBook, EquatableArray<DeliveryAddressBook.Entry>>
{
    private DeliveryAddressBook(EquatableArray<Entry> entries) => Entries = entries;

    public static DeliveryAddressBook Empty { get; } = new(EquatableArray<Entry>.Empty);

    /// <summary>The addresses in ERP order with the default marked (the stored and shown form).</summary>
    public EquatableArray<Entry> Entries { get; }

    public IEnumerable<DeliveryAddress> Addresses => Entries.Select(entry => entry.Address);

    public Maybe<DeliveryAddress> Default => Entries.FirstOrNone(entry => entry.IsDefault).Map(entry => entry.Address);

    public bool IsEmpty => Entries.IsEmpty;

    /// <summary>The book of these addresses, or every rule they break.</summary>
    public static Validated<DeliveryAddressBook> Create(IEnumerable<(DeliveryAddress Address, bool IsDefault)> addresses)
    {
        EquatableArray<Entry> entries = [.. addresses.Select(a => new Entry(a.Address, a.IsDefault))];
        Error[] errors =
        [
            .. entries.Count(entry => entry.IsDefault) > 1
                ? [Error.Validation("delivery-address.many-defaults", "At most one delivery address can be the default.") with { Field = "deliveryAddresses" }]
                : Array.Empty<Error>(),
            .. UniqueCodes(entries)
                ? Array.Empty<Error>()
                : [Error.Validation("delivery-address.duplicate-code", "Delivery address codes must be unique.") with { Field = "deliveryAddresses" }],
        ];
        return errors is [var first, .. var rest] ? Validated.Invalid<DeliveryAddressBook>(first, rest) : Validated.Valid(new DeliveryAddressBook(entries));
    }

    public sealed record Entry(DeliveryAddress Address, bool IsDefault);

    private static bool UniqueCodes(EquatableArray<Entry> entries) =>
        entries.Select(entry => entry.Address.Code.Value).Distinct(StringComparer.Ordinal).Count() == entries.Count;

    static bool IPersistedValue<DeliveryAddressBook, EquatableArray<Entry>>.IsWellFormed(EquatableArray<Entry> raw) =>
        raw.Count(entry => entry.IsDefault) <= 1 && UniqueCodes(raw);

    static DeliveryAddressBook IPersistedValue<DeliveryAddressBook, EquatableArray<Entry>>.Rehydrate(EquatableArray<Entry> raw) => new(raw);

    EquatableArray<Entry> IPersistedValue<DeliveryAddressBook, EquatableArray<Entry>>.Dehydrate() => Entries;
}

/// <summary>A contact person's e-mail: none, known, or known and used for platform access (with its rights template).</summary>
public abstract record ContactEmail
{
    private ContactEmail()
    {
    }

    public static ContactEmail None { get; } = new NoEmail();

    public sealed record NoEmail : ContactEmail;

    public sealed record Known(EmailAddress Address) : ContactEmail;

    /// <param name="Template">None: the platform's default template.</param>
    public sealed record WithPortalAccess(EmailAddress Address, Maybe<ContactTemplate> Template) : ContactEmail;
}

/// <summary>A contact person of a customer; only a contact with an e-mail can have platform access.</summary>
public sealed record Contact : IPersistedValue<Contact, Contact.Stored>
{
    private Contact(Stored stored) => Value = stored;

    private Stored Value { get; }

    public CustomerText ExternalId => Value.ExternalId;

    public CustomerText Name => Value.Name;

    public Maybe<CustomerText> Phone => Value.Phone;

    public ContactEmail Email =>
        (Value.Email, Value.PortalAccess) switch
        {
            (var email, true) => email.Match<ContactEmail>(address => new ContactEmail.WithPortalAccess(address, Value.AttributeTemplate), () => ContactEmail.None),
            (var email, false) => email.Match<ContactEmail>(address => new ContactEmail.Known(address), () => ContactEmail.None),
        };

    public bool PortalAccess => Value.PortalAccess;

    public static Contact Create(CustomerText externalId, CustomerText name, Maybe<CustomerText> phone, ContactEmail email) =>
        new(email switch
        {
            ContactEmail.WithPortalAccess portal => new Stored(externalId, name, portal.Address, phone, true, portal.Template),
            ContactEmail.Known known => new Stored(externalId, name, known.Address, phone, false, Maybe<ContactTemplate>.None),
            _ => new Stored(externalId, name, Maybe<EmailAddress>.None, phone, false, Maybe<ContactTemplate>.None),
        });

    /// <summary>The stored (and earlier) flat shape: platform access needs the e-mail, a template needs the access.</summary>
    public sealed record Stored(CustomerText ExternalId, CustomerText Name, Maybe<EmailAddress> Email, Maybe<CustomerText> Phone, bool PortalAccess, Maybe<ContactTemplate> AttributeTemplate);

    static bool IPersistedValue<Contact, Stored>.IsWellFormed(Stored raw) =>
        (!raw.PortalAccess || raw.Email.Exists(_ => true)) && (raw.PortalAccess || !raw.AttributeTemplate.Exists(_ => true));

    static Contact IPersistedValue<Contact, Stored>.Rehydrate(Stored raw) => new(raw);

    Stored IPersistedValue<Contact, Stored>.Dehydrate() => Value;
}

/// <summary>The contacts of a customer, with unique ERP ids.</summary>
public sealed record ContactList : IPersistedValue<ContactList, EquatableArray<Contact>>
{
    private ContactList(EquatableArray<Contact> contacts) => Contacts = contacts;

    public static ContactList Empty { get; } = new(EquatableArray<Contact>.Empty);

    public EquatableArray<Contact> Contacts { get; }

    public static Result<ContactList, Error> Create(IEnumerable<Contact> contacts)
    {
        EquatableArray<Contact> all = [.. contacts];
        return UniqueIds(all)
            ? new ContactList(all)
            : Error.Validation("contact.duplicate-id", "Contact ids must be unique.") with { Field = "contacts" };
    }

    /// <summary>The contacts with platform access, in ERP order.</summary>
    public IEnumerable<(Contact Contact, ContactEmail.WithPortalAccess Access)> WithPortalAccess =>
        Contacts.Choose(contact => contact.Email is ContactEmail.WithPortalAccess access ? Maybe.Some((contact, access)) : Maybe<(Contact, ContactEmail.WithPortalAccess)>.None);

    private static bool UniqueIds(EquatableArray<Contact> contacts) =>
        contacts.Select(contact => contact.ExternalId.Value).Distinct(StringComparer.Ordinal).Count() == contacts.Count;

    static bool IPersistedValue<ContactList, EquatableArray<Contact>>.IsWellFormed(EquatableArray<Contact> raw) => UniqueIds(raw);

    static ContactList IPersistedValue<ContactList, EquatableArray<Contact>>.Rehydrate(EquatableArray<Contact> raw) => new(raw);

    EquatableArray<Contact> IPersistedValue<ContactList, EquatableArray<Contact>>.Dehydrate() => Contacts;
}

public sealed record Salesperson(CustomerText Name, Maybe<EmailAddress> Email, Maybe<CustomerText> Phone);

/// <param name="PriceListCode">The customer's own price list (pricing falls back to the platform default).</param>
public sealed record CommercialTerms(Maybe<CustomerText> PriceListCode, Maybe<PaymentTerms> PaymentTerms, Currency Currency);

public sealed record CustomerContent(
    CustomerText Name,
    Maybe<TaxId> TaxId,
    Address BillingAddress,
    DeliveryAddressBook DeliveryAddresses,
    ContactList Contacts,
    Maybe<Salesperson> Salesperson,
    CommercialTerms Terms);

/// <summary>Where a customer stands in the ERP: active, blocked (may sign in, may not order) or inactive.</summary>
public enum CustomerStanding
{
    Active,

    /// <summary>Blocked in the ERP (e.g. overdue payments): its people may sign in, but not order.</summary>
    Blocked,
    Inactive,
}

/// <summary>A customer (company buying from the seller) as replicated from the ERP.</summary>
public sealed record Customer(CustomerCode Code, SourceVersion Version, CustomerContent Content, CustomerStanding Standing)
{
    public bool Active => Standing != CustomerStanding.Inactive;

    public bool Blocked => Standing == CustomerStanding.Blocked;
}

[RawInput]
public sealed record UnvalidatedAddress(string Street, string PostalCode, string City, string Country);

[RawInput]
public sealed record UnvalidatedDeliveryAddress(string Code, Maybe<string> Name, Maybe<UnvalidatedAddress> Address, bool IsDefault);

[RawInput]
public sealed record UnvalidatedContact(string ExternalId, string Name, Maybe<string> Email, Maybe<string> Phone, bool PortalAccess, Maybe<string> AttributeTemplate);

[RawInput]
public sealed record UnvalidatedSalesperson(string Name, Maybe<string> Email, Maybe<string> Phone);

/// <summary>A customer as received from the ERP; missing required texts arrive as empty, optional parts are absent.</summary>
/// <param name="Blocked">Of an active customer only: an inactive customer is inactive, blocked or not.</param>
[RawInput]
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
    public const int MaxCode = 64;

    private static readonly Func<CustomerCode, SourceVersion, CustomerContent, Func<CustomerStanding, Customer>> Create =
        (code, version, content) => standing => new Customer(code, version, content, standing);

    private static readonly Func<CustomerText, Maybe<TaxId>, Address, DeliveryAddressBook, ContactList, Maybe<Salesperson>, CommercialTerms, CustomerContent> CreateContent =
        (name, taxId, billing, delivery, contacts, salesperson, terms) => new CustomerContent(name, taxId, billing, delivery, contacts, salesperson, terms);

    /// <summary>Validates a customer from the ERP, reporting every problem with its field path.</summary>
    public static Validated<Customer> Validate(UnvalidatedCustomer input) =>
        Create
            .Apply(CustomerCode.Parse(input.Code).ToValidated("code"))
            .Apply(SourceVersion.Parse(input.Version).ToValidated("version"))
            .Apply(CreateContent
                .Apply(CustomerText.Name(input.Name, MaxName, "name", "Name").ToValidated("name"))
                .Apply(Maybe.ParseOptional(input.TaxId, TaxId.Parse).ToValidated("taxId"))
                .Apply(ValidateAddress(input.BillingAddress).AtField("billingAddress"))
                .Apply(ValidateDeliveryAddresses(input.DeliveryAddresses))
                .Apply(ValidateContacts(input.Contacts))
                .Apply(input.Salesperson.Match(salesperson => ValidateSalesperson(salesperson).Map(Maybe.Some).AtField("salesperson"), () => Validated.Valid(Maybe<Salesperson>.None)))
                .Apply(ValidateTerms(input)))
            .Map(complete => complete(!input.Active ? CustomerStanding.Inactive : input.Blocked ? CustomerStanding.Blocked : CustomerStanding.Active));

    public static Change<Customer> Reconcile(Maybe<Customer> stored, Customer incoming) =>
        ImportBatch.Reconcile(stored, incoming, c => c.Version, (a, b) => a.Content == b.Content && a.Standing == b.Standing);

    /// <summary>Whether the people with platform access differ (what the access module reacts to).</summary>
    public static bool PortalContactsChanged(Maybe<Customer> before, Customer after) =>
        before.Match(
            previous => !previous.Content.Contacts.WithPortalAccess.SequenceEqual(after.Content.Contacts.WithPortalAccess),
            () => after.Content.Contacts.WithPortalAccess.Any());

    private static Validated<Address> ValidateAddress(Maybe<UnvalidatedAddress> input) =>
        input.Match(
            address => CountryCode.Parse(address.Country).ToValidated("country").Match(
                country => ((Func<CustomerText, PostalCode, CustomerText, Address>)((street, postal, city) => new Address(street, postal, city, country)))
                    .Apply(CustomerText.Name(address.Street, MaxStreet, "street", "Street").ToValidated("street"))
                    .Apply(PostalCode.Parse(address.PostalCode, country).ToValidated("postalCode"))
                    .Apply(CustomerText.Name(address.City, MaxCity, "city", "City").ToValidated("city")),
                errors => Validated.Invalid<Address>(errors[0], [.. errors.Skip(1)])),
            () => Validated.Invalid<Address>(Error.Validation("address.missing", "The address is required.")));

    private static Validated<DeliveryAddressBook> ValidateDeliveryAddresses(IReadOnlyList<UnvalidatedDeliveryAddress> input) =>
        Bounded(input, MaxAddresses, "deliveryAddresses", address =>
                ((Func<CustomerText, Maybe<CustomerText>, Address, (DeliveryAddress Address, bool IsDefault)>)((code, name, value) => (new DeliveryAddress(code, name, value), address.IsDefault)))
                    .Apply(CustomerText.Parse(address.Code, MaxCode, "address-code", "Address code").ToValidated("code"))
                    .Apply(CustomerText.ParseOptional(address.Name, MaxName, "address-name", "Address name").ToValidated("name"))
                    .Apply(ValidateAddress(address.Address).AtField("address")))
            .Bind(addresses => DeliveryAddressBook.Create(addresses));

    private static Validated<ContactList> ValidateContacts(IReadOnlyList<UnvalidatedContact> input) =>
        Bounded(input, MaxContacts, "contacts", contact =>
                ((Func<CustomerText, CustomerText, Maybe<EmailAddress>, Maybe<CustomerText>, Maybe<ContactTemplate>, (CustomerText Id, CustomerText Name, Maybe<EmailAddress> Email, Maybe<CustomerText> Phone, Maybe<ContactTemplate> Template)>)(
                    (id, name, email, phone, template) => (id, name, email, phone, template)))
                    .Apply(CustomerText.Parse(contact.ExternalId, MaxCode, "contact-id", "Contact id").ToValidated("externalId"))
                    .Apply(CustomerText.Name(contact.Name, MaxName, "contact-name", "Contact name").ToValidated("name"))
                    .Apply(Maybe.ParseOptional(contact.Email, EmailAddress.Parse).ToValidated("email"))
                    .Apply(CustomerText.ParseOptional(contact.Phone, MaxPhone, "phone", "Phone").ToValidated("phone"))
                    .Apply(Maybe.ParseOptional(contact.AttributeTemplate, ContactTemplate.Parse).ToValidated("attributeTemplate"))
                    .Bind(parts => EmailOf(parts.Email, contact.PortalAccess, parts.Template).Map(email => Contact.Create(parts.Id, parts.Name, parts.Phone, email)).ToValidated()))
            .Bind(contacts => ContactList.Create(contacts).ToValidated());

    /// <summary>Platform access needs an e-mail; without the access a template means nothing and is dropped.</summary>
    private static Result<ContactEmail, Error> EmailOf(Maybe<EmailAddress> email, bool portalAccess, Maybe<ContactTemplate> template) =>
        portalAccess
            ? email.Match<Result<ContactEmail, Error>>(
                address => new ContactEmail.WithPortalAccess(address, template),
                () => Error.Validation("contact.portal-needs-email", "A contact with platform access needs an e-mail address.") with { Field = "email" })
            : email.Match<ContactEmail>(address => new ContactEmail.Known(address), () => ContactEmail.None);

    private static Validated<Salesperson> ValidateSalesperson(UnvalidatedSalesperson input) =>
        ((Func<CustomerText, Maybe<EmailAddress>, Maybe<CustomerText>, Salesperson>)((name, email, phone) => new Salesperson(name, email, phone)))
            .Apply(CustomerText.Name(input.Name, MaxName, "salesperson-name", "Salesperson name").ToValidated("name"))
            .Apply(Maybe.ParseOptional(input.Email, EmailAddress.Parse).ToValidated("email"))
            .Apply(CustomerText.ParseOptional(input.Phone, MaxPhone, "phone", "Phone").ToValidated("phone"));

    private static Validated<CommercialTerms> ValidateTerms(UnvalidatedCustomer input) =>
        ((Func<Maybe<CustomerText>, Maybe<PaymentTerms>, Currency, CommercialTerms>)((priceList, terms, currency) => new CommercialTerms(priceList, terms, currency)))
            .Apply(CustomerText.ParseOptional(input.PriceListCode, MaxCode, "price-list", "Price list").ToValidated("priceListCode"))
            .Apply(input.PaymentTermDays.Match(days => PaymentTerms.Parse(days).Map(Maybe.Some), () => Result.Ok<Maybe<PaymentTerms>, Error>(Maybe<PaymentTerms>.None)).ToValidated("paymentTermDays"))
            .Apply(Currency.Parse(input.Currency.ValueOr("PLN")).ToValidated("currency"));

    private static Validated<EquatableArray<TOut>> Bounded<TIn, TOut>(IReadOnlyList<TIn> items, int max, string field, Func<TIn, Validated<TOut>> validate)
        where TIn : notnull
        where TOut : notnull =>
        items.Count > max
            ? Validated.Invalid<EquatableArray<TOut>>(Error.Validation($"{field}.too-many", $"At most {max} items are allowed.") with { Field = field })
            : items.Traverse(validate).Map(values => values.ToEquatableArray()).AtField(field);
}
