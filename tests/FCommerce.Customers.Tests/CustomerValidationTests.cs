using FCommerce.Customers.Domain;
using FCommerce.SharedKernel.Imports;
using FCommerce.Testing;

namespace FCommerce.Customers.Tests;

public sealed class CustomerValidationTests
{
    private static readonly UnvalidatedAddress Warsaw = new("ul. Marszałkowska 1", "00950", "Warszawa", "pl");

    private static UnvalidatedCustomer Input(long version = 1) =>
        new(
            "K-001",
            version,
            "  Hurtownia   Śruba sp. z o.o. ",
            "526-104-08-28",
            Warsaw,
            [new UnvalidatedDeliveryAddress("MAG", "Magazyn", new UnvalidatedAddress("ul. Składowa 5", "61-131", "Poznań", "PL"), IsDefault: true)],
            [
                new UnvalidatedContact("C1", "Jan Kowalski", "Jan.Kowalski@Sruba.PL", "+48 600 100 200", PortalAccess: true, "administrator"),
                new UnvalidatedContact("C2", "Ewa Nowak", default, default, PortalAccess: false, default),
            ],
            new UnvalidatedSalesperson("Anna Handlowiec", "anna@seller.test", default),
            "HURT",
            30,
            default,
            Blocked: false,
            Active: true);

    private static Customer Valid(UnvalidatedCustomer input) => Domain.Customers.Validate(input).ShouldBeValid();

    [Fact]
    public void A_valid_customer_is_normalized()
    {
        var customer = Valid(Input());

        Assert.Equal("Hurtownia Śruba sp. z o.o.", customer.Content.Name);
        Assert.Equal("PL5261040828", customer.Content.TaxId.ShouldBeSome().Value);
        Assert.Equal(("00-950", "PL"), (customer.Content.BillingAddress.PostalCode.Value, customer.Content.BillingAddress.Country.Value));
        Assert.Equal("jan.kowalski@sruba.pl", customer.Content.Contacts[0].Email.ShouldBeSome().Value);
        Assert.Equal("anna@seller.test", customer.Content.Salesperson.ShouldBeSome().Email.ShouldBeSome().Value);
        Assert.Equal(("HURT", 30, "PLN"), (customer.Content.Terms.PriceListCode.ShouldBeSome(), customer.Content.Terms.PaymentTermDays.ShouldBeSome(), customer.Content.Terms.Currency.Code));
    }

    [Fact]
    public void Optional_parts_may_be_absent()
    {
        var customer = Valid(Input() with { TaxId = " ", DeliveryAddresses = [], Contacts = [], Salesperson = default, PriceListCode = default, PaymentTermDays = default });

        customer.Content.TaxId.ShouldBeNone();
        Assert.Empty(customer.Content.DeliveryAddresses);
        Assert.Empty(customer.Content.Contacts);
        customer.Content.Salesperson.ShouldBeNone();
    }

    [Fact]
    public void Every_problem_is_reported_with_its_path()
    {
        var input = Input() with
        {
            Code = "",
            TaxId = "5261040827",
            BillingAddress = Warsaw with { PostalCode = "123", City = " " },
            DeliveryAddresses = [new UnvalidatedDeliveryAddress("MAG", default, new UnvalidatedAddress("ul. Składowa 5", "61-131", "Poznań", "XX"), false)],
            Contacts = [new UnvalidatedContact("C1", "Jan", "jan@", default, false, "king")],
            Salesperson = new UnvalidatedSalesperson("", default, default),
            PaymentTermDays = 400,
            Currency = "ZLOTY",
        };

        var fields = Domain.Customers.Validate(input).ShouldBeInvalid().Select(e => e.Field);

        Assert.Equal(
            [
                "code",
                "taxId",
                "billingAddress.postalCode",
                "billingAddress.city",
                "deliveryAddresses[0].address.country",
                "contacts[0].email",
                "contacts[0].attributeTemplate",
                "salesperson.name",
                "paymentTermDays",
                "currency",
            ],
            fields);
    }

    [Fact]
    public void A_missing_billing_address_is_reported()
    {
        var error = Assert.Single(Domain.Customers.Validate(Input() with { BillingAddress = default }).ShouldBeInvalid());

        Assert.Equal(("address.missing", "billingAddress"), (error.Code, error.Field));
    }

    [Fact]
    public void A_contact_with_platform_access_needs_an_email()
    {
        var input = Input() with { Contacts = [new UnvalidatedContact("C1", "Jan Kowalski", default, default, PortalAccess: true, default)] };

        var error = Assert.Single(Domain.Customers.Validate(input).ShouldBeInvalid());

        Assert.Equal(("contact.portal-needs-email", "contacts[0].email"), (error.Code, error.Field));
    }

    [Fact]
    public void Delivery_addresses_have_unique_codes_and_at_most_one_default()
    {
        var address = new UnvalidatedDeliveryAddress("MAG", default, Warsaw, IsDefault: true);

        var codes = Domain.Customers.Validate(Input() with { DeliveryAddresses = [address, address] }).ShouldBeInvalid().Select(e => e.Code);

        Assert.Equal(["delivery-address.many-defaults", "delivery-address.duplicate-code"], codes);
    }

    [Fact]
    public void Contact_ids_are_unique()
    {
        var contact = new UnvalidatedContact("C1", "Jan", default, default, false, default);

        var error = Assert.Single(Domain.Customers.Validate(Input() with { Contacts = [contact, contact] }).ShouldBeInvalid());

        Assert.Equal(("contact.duplicate-id", "contacts"), (error.Code, error.Field));
    }

    [Fact]
    public void Reconciliation_follows_the_erp_version()
    {
        var stored = Valid(Input(version: 5));

        Assert.IsType<Change<Customer>.Created>(Domain.Customers.Reconcile(default, stored));
        Assert.IsType<Change<Customer>.Stale>(Domain.Customers.Reconcile(stored, Valid(Input(version: 5) with { Name = "Inna" })));
        Assert.IsType<Change<Customer>.Unchanged>(Domain.Customers.Reconcile(stored, Valid(Input(version: 6))));
        Assert.IsType<Change<Customer>.Updated>(Domain.Customers.Reconcile(stored, Valid(Input(version: 6) with { Blocked = true })));
    }

    [Fact]
    public void Portal_contacts_change_only_when_people_with_access_differ()
    {
        var before = Valid(Input());
        var phoneOfOther = Valid(Input() with { Contacts = [Input().Contacts![0], Input().Contacts![1] with { Phone = "+48 500 000 000" }] });
        var newTemplate = Valid(Input() with { Contacts = [Input().Contacts![0] with { AttributeTemplate = "buyer" }, Input().Contacts![1]] });

        Assert.True(Domain.Customers.PortalContactsChanged(default, before));
        Assert.False(Domain.Customers.PortalContactsChanged(default, Valid(Input() with { Contacts = [] })));
        Assert.False(Domain.Customers.PortalContactsChanged(before, phoneOfOther));
        Assert.True(Domain.Customers.PortalContactsChanged(before, newTemplate));
    }
}
