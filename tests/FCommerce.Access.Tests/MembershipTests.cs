using FCommerce.Access.Domain;
using FCommerce.Authorization.Core;
using FCommerce.SharedKernel.Contact;
using FCommerce.SharedKernel.Monetary;
using FCommerce.SharedKernel.Optional;
using FCommerce.Testing;

namespace FCommerce.Access.Tests;

public sealed class MembershipTests
{
    private static EmailAddress Email(string value) => EmailAddress.Parse(value).ShouldSucceed();

    private static Account Account(string email, AccountStatus status = AccountStatus.Active, Maybe<OperatorAttributes> op = default) =>
        new(Guid.NewGuid(), Email(email), PersonName.Parse("Jan").ShouldSucceed(), Values.Culture("pl"), status, "hash", "stamp", 0, default, op);

    private static Membership Member(Account account, string customer, MembershipSource source = MembershipSource.Erp, Maybe<MembershipAttributes> attributes = default) =>
        new(account.Id, customer, attributes.ValueOr(MembershipAttributes.Buyer), source, source == MembershipSource.Erp ? "C1" : Maybe<string>.None);

    private static ErpContactSync.Contact Contact(string email, string id = "C1") =>
        new(id, Email(email), PersonName.Parse("Jan Kowalski").ShouldSucceed(), MembershipAttributes.Administrator);

    [Fact]
    public void Operators_disabled_accounts_and_existing_members_are_not_admitted()
    {
        var op = Account("op@seller.pl", op: OperatorAttributes.Salesperson);
        var disabled = Account("off@firma.pl", AccountStatus.Disabled);
        var member = Account("jan@firma.pl");

        Assert.Equal("membership.operator-account", MembershipRules.Admit(op, [], true, Member(op, "K1")).ShouldFail().Code);
        Assert.Equal("membership.account-disabled", MembershipRules.Admit(disabled, [], true, Member(disabled, "K1")).ShouldFail().Code);
        Assert.Equal("membership.exists", MembershipRules.Admit(member, [Member(member, "K1")], true, Member(member, "K1")).ShouldFail().Code);
    }

    [Fact]
    public void Another_customer_needs_the_multi_customer_feature()
    {
        var account = Account("jan@firma.pl");
        var current = Member(account, "K1");

        Assert.Equal("membership.single-customer", MembershipRules.Admit(account, [current], false, Member(account, "K2")).ShouldFail().Code);
        Assert.Equal("K2", MembershipRules.Admit(account, [current], true, Member(account, "K2")).ShouldSucceed().CustomerCode);
        Assert.Equal("K1", MembershipRules.Admit(default, [], false, Member(account, "K1")).ShouldSucceed().CustomerCode);
    }

    [Fact]
    public void Rights_carry_every_attribute_the_ui_edits()
    {
        var limit = new Money(5000, Currency.Parse("PLN").ShouldSucceed());
        var rights = new MembershipRights(false, OrderScope.Company, true, true, limit, DeliveryAddressScope.Only(["MAG"]));

        var changed = rights.ApplyTo(MembershipAttributes.Buyer);

        Assert.Equal(rights, MembershipRights.Of(changed));
        Assert.Equal(new MembershipAttributes(false, OrderScope.Company, limit, true, true, DeliveryAddressScope.Only(["MAG"])), changed);
    }

    [Theory]
    [InlineData(0, "membership.order-limit-not-positive")]
    [InlineData(-5, "membership.order-limit-not-positive")]
    [InlineData(10.005, "membership.order-limit-precision")]
    [InlineData(2000000000, "membership.order-limit-too-large")]
    public void An_order_limit_is_positive_with_at_most_two_decimal_places(decimal amount, string code)
    {
        Assert.Equal(code, MembershipRights.Limit(amount, "PLN").ShouldFail().Code);
    }

    [Fact]
    public void An_order_limit_needs_a_valid_currency_and_none_means_unlimited()
    {
        Assert.Equal(new Money(1500.5m, Currency.Parse("EUR").ShouldSucceed()), MembershipRights.Limit(1500.5m, "eur").ShouldSucceed().ShouldBeSome());
        Assert.Equal("currency.invalid", MembershipRights.Limit(100, "").ShouldFail().Code);
        MembershipRights.Limit(Maybe<decimal>.None, "").ShouldSucceed().ShouldBeNone();
    }

    [Fact]
    public void No_listed_delivery_address_means_all_of_them()
    {
        Assert.Equal(DeliveryAddressScope.All, MembershipRights.Addresses(Maybe<IReadOnlyList<string>>.None).ShouldSucceed());
        Assert.Equal(DeliveryAddressScope.All, MembershipRights.Addresses(Maybe.Some<IReadOnlyList<string>>([" ", ""])).ShouldSucceed());
        Assert.Equal(DeliveryAddressScope.Only(["MAG", "BUD"]), MembershipRights.Addresses(Maybe.Some<IReadOnlyList<string>>([" MAG", "BUD", "MAG"])).ShouldSucceed());
    }

    [Fact]
    public void Templates_fall_back_to_buyer()
    {
        Assert.Equal(MembershipAttributes.Accounting, MembershipRules.FromTemplate("accounting"));
        Assert.Equal(MembershipAttributes.Buyer, MembershipRules.FromTemplate("king"));
        Assert.Equal(MembershipAttributes.Buyer, MembershipRules.FromTemplate(default));
    }

    [Fact]
    public void Erp_contacts_create_accounts_or_grant_existing_ones()
    {
        var other = Account("ewa@firma.pl");
        var steps = ErpContactSync.Plan(
            "K1",
            [Contact("jan@firma.pl"), Contact("ewa@firma.pl", "C2"), Contact("jan@firma.pl", "C3")],
            [new ErpContactSync.Member(other, [Member(other, "K9")])],
            multiCustomer: true);

        Assert.Collection(
            steps,
            step => Assert.Equal("jan@firma.pl", Assert.IsType<ErpContactSync.Step.CreateAccount>(step).Contact.Email.Value),
            step =>
            {
                var grant = Assert.IsType<ErpContactSync.Step.Grant>(step);
                Assert.Equal((other.Id, "K1", MembershipSource.Erp, "C2"), (grant.Membership.UserId, grant.Membership.CustomerCode, grant.Membership.Source, grant.Membership.ErpContactId));
                Assert.Equal(MembershipAttributes.Administrator, grant.Membership.Attributes);
            });
    }

    [Fact]
    public void Erp_contacts_that_cannot_get_access_are_skipped_with_the_reason()
    {
        var op = Account("op@seller.pl", op: OperatorAttributes.Salesperson);
        var elsewhere = Account("ewa@firma.pl");

        var steps = ErpContactSync.Plan(
            "K1",
            [Contact("op@seller.pl"), Contact("ewa@firma.pl", "C2")],
            [new ErpContactSync.Member(op, []), new ErpContactSync.Member(elsewhere, [Member(elsewhere, "K9")])],
            multiCustomer: false);

        Assert.Equal(
            ["membership.operator-account", "membership.single-customer"],
            steps.Select(s => Assert.IsType<ErpContactSync.Step.Skip>(s).Reason.Code));
    }

    [Fact]
    public void Only_erp_memberships_of_people_no_longer_listed_are_revoked()
    {
        var kept = Account("jan@firma.pl");
        var removed = Account("old@firma.pl");
        var manual = Account("manual@firma.pl");

        var steps = ErpContactSync.Plan(
            "K1",
            [Contact("jan@firma.pl")],
            [
                new ErpContactSync.Member(kept, [Member(kept, "K1")]),
                new ErpContactSync.Member(removed, [Member(removed, "K1"), Member(removed, "K2")]),
                new ErpContactSync.Member(manual, [Member(manual, "K1", MembershipSource.CustomerAdmin)]),
            ],
            multiCustomer: true);

        var revoke = Assert.IsType<ErpContactSync.Step.Revoke>(Assert.Single(steps));
        Assert.Equal((removed.Id, "K1"), (revoke.Membership.UserId, revoke.Membership.CustomerCode));
    }
}
