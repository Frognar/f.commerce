using FCommerce.Access.Domain;
using FCommerce.Authorization.Core;
using FCommerce.SharedKernel.Optional;
using FCommerce.Testing;
using static FCommerce.Access.Tests.AccessValues;

namespace FCommerce.Access.Tests;

public sealed class MembershipTests
{
    private static Membership Member(Account account, string customer, MembershipSource? source = null, Maybe<MembershipAttributes> attributes = default) =>
        new(account.Id, Customer(customer), attributes.ValueOr(MembershipAttributes.Buyer), source ?? new MembershipSource.Erp(ContactId("C1")));

    private static ErpContactSync.Contact Contact(string email, string id = "C1") =>
        new(ContactId(id), Email(email), PersonName.Parse("Jan Kowalski").ShouldSucceed(), MembershipAttributes.Administrator);

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
        Assert.Equal("K2", MembershipRules.Admit(account, [current], true, Member(account, "K2")).ShouldSucceed().CustomerCode.Value);
        Assert.Equal("K1", MembershipRules.Admit(default, [], false, Member(account, "K1")).ShouldSucceed().CustomerCode.Value);
    }

    [Fact]
    public void A_limit_and_addresses_belong_to_the_right_to_order()
    {
        var buyer = MembershipRights.Parse(true, OrderScope.Own, false, false, 5000m, "pln", Maybe.Some<IReadOnlyList<string>>([" MAG", "BUD", "MAG"])).ShouldSucceed();
        var accountant = MembershipRights.Parse(false, OrderScope.Company, true, false, 5000m, "PLN", Maybe.Some<IReadOnlyList<string>>(["MAG"])).ShouldSucceed();

        var allowed = Assert.IsType<OrderingRight.Allowed>(buyer.Ordering);
        Assert.Equal((5000m, "PLN"), (allowed.Limit.ShouldBeSome().Value.Amount, allowed.Limit.ShouldBeSome().Value.Currency.Code));
        Assert.Equal(["MAG", "BUD"], Assert.IsType<DeliveryAddressScope.OnlyAddresses>(allowed.Addresses).Codes);
        Assert.Equal(new MembershipAttributes(OrderingRight.NotAllowed, OrderScope.Company, true, false), accountant);
        accountant.OrderLimit.ShouldBeNone();
    }

    [Theory]
    [InlineData(0, "membership.order-limit-not-positive")]
    [InlineData(-5, "membership.order-limit-not-positive")]
    [InlineData(10.005, "membership.order-limit-precision")]
    [InlineData(2000000000, "membership.order-limit-too-large")]
    public void An_order_limit_is_positive_with_at_most_two_decimal_places(decimal amount, string code)
    {
        Assert.Equal(code, OrderLimit.Parse(amount, "PLN").ShouldFail().Code);
    }

    [Fact]
    public void An_order_limit_needs_a_valid_currency()
    {
        Assert.Equal("EUR", OrderLimit.Parse(1500.5m, "eur").ShouldSucceed().Value.Currency.Code);
        Assert.Equal(("currency.invalid", "orderLimitCurrency"), (OrderLimit.Parse(100, "").ShouldFail().Code, OrderLimit.Parse(100, "").ShouldFail().Field.ValueOr("")));
    }

    [Fact]
    public void No_listed_delivery_address_means_all_of_them()
    {
        Assert.Equal(DeliveryAddressScope.All, DeliveryAddressScope.Of([]));
        Assert.Equal(DeliveryAddressScope.All, DeliveryAddressScope.Of([" ", ""]));
        Assert.Equal(
            "membership.too-many-addresses",
            MembershipRights.Parse(true, OrderScope.Own, false, false, default, "", Maybe.Some<IReadOnlyList<string>>([.. Enumerable.Range(0, 101).Select(i => $"A{i}")])).ShouldFail().Code);
    }

    [Fact]
    public void Templates_are_named_and_an_unknown_one_is_an_error()
    {
        Assert.Equal(MembershipAttributes.Accounting, MembershipAttributes.Template(" accounting ").ShouldSucceed());
        Assert.Equal("membership.template-unknown", MembershipAttributes.Template("king").ShouldFail().Code);
    }

    [Fact]
    public void A_customer_user_is_not_made_an_operator_and_revoking_operator_rights_signs_out()
    {
        var buyer = Account("jan@firma.pl");
        var colleague = Account("ewa@firma.pl");
        var op = Account("op@seller.pl", op: OperatorAttributes.Salesperson);

        Assert.Equal("operator.customer-user", OperatorRules.Appoint(buyer, [Member(buyer, "K1")], OperatorAttributes.Salesperson).ShouldFail().Code);
        Assert.Equal("operator.account-disabled", OperatorRules.Appoint(Account(status: AccountStatus.Disabled), [], OperatorAttributes.Salesperson).ShouldFail().Code);
        Assert.True(OperatorRules.Appoint(colleague, [], OperatorAttributes.Salesperson).ShouldSucceed().IsOperator);
        var revoked = OperatorRules.Revoke(op, Stamp("new"));
        Assert.Equal((false, "new"), (revoked.IsOperator, revoked.SecurityStamp.Value));
    }

    [Fact]
    public void Erp_contacts_create_accounts_or_grant_existing_ones()
    {
        var other = Account("ewa@firma.pl");
        var steps = ErpContactSync.Plan(
            Customer("K1"),
            [Contact("jan@firma.pl"), Contact("ewa@firma.pl", "C2"), Contact("jan@firma.pl", "C3")],
            [new ErpContactSync.Member(other, [Member(other, "K9")])],
            multiCustomer: true);

        Assert.Collection(
            steps,
            step => Assert.Equal("jan@firma.pl", Assert.IsType<ErpContactSync.Step.CreateAccount>(step).Contact.Email.Value),
            step =>
            {
                var grant = Assert.IsType<ErpContactSync.Step.Grant>(step);
                Assert.Equal((other.Id, "K1", new MembershipSource.Erp(ContactId("C2"))), (grant.Membership.UserId, grant.Membership.CustomerCode.Value, grant.Membership.Source));
                Assert.Equal(MembershipAttributes.Administrator, grant.Membership.Attributes);
            });
    }

    [Fact]
    public void Erp_contacts_that_cannot_get_access_are_skipped_with_the_reason()
    {
        var op = Account("op@seller.pl", op: OperatorAttributes.Salesperson);
        var elsewhere = Account("ewa@firma.pl");

        var steps = ErpContactSync.Plan(
            Customer("K1"),
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
            Customer("K1"),
            [Contact("jan@firma.pl")],
            [
                new ErpContactSync.Member(kept, [Member(kept, "K1")]),
                new ErpContactSync.Member(removed, [Member(removed, "K1"), Member(removed, "K2")]),
                new ErpContactSync.Member(manual, [Member(manual, "K1", MembershipSource.CustomerAdmin)]),
            ],
            multiCustomer: true);

        var revoke = Assert.IsType<ErpContactSync.Step.Revoke>(Assert.Single(steps));
        Assert.Equal((removed.Id, "K1"), (revoke.Membership.UserId, revoke.Membership.CustomerCode.Value));
    }
}
