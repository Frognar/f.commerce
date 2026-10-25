using FCommerce.Ordering.Domain;
using FCommerce.SharedKernel.Collections;
using FCommerce.SharedKernel.Optional;
using FCommerce.Testing;

namespace FCommerce.Ordering.Tests;

public sealed class OrderDraftTests
{
    private static readonly DateOnly Today = new(2026, 10, 25);

    private static readonly DeliveryAddress Warehouse = new("MAG", Maybe.Some("Magazyn"), "ul. Długa 1", "00-950", "Warszawa", "PL");
    private static readonly DeliveryAddress Site = new("BUD", Maybe<string>.None, "ul. Krótka 2", "00-951", "Warszawa", "PL");

    private static readonly OrderTotals Pln = new("PLN", 15m, [new VatTotal(23, 15m, 3.45m)], 3.45m, 18.45m);

    private static CartLine Line(bool priced = true) =>
        new("A", Maybe.Some("Śruba"), Maybe.Some("szt"), 10, priced ? 1.5m : Maybe<decimal>.None, priced ? 23m : Maybe<decimal>.None,
            priced ? 15m : Maybe<decimal>.None, priced ? "HURT" : Maybe<string>.None, Backorder: true);

    private static CartSnapshot Cart(bool complete = true, bool canOrder = true, long version = 3, OrderTotals[]? totals = null) =>
        new(version, complete, canOrder, [Line()], [.. totals ?? [Pln]]);

    private static CheckoutForm Form(string? address = null, DateOnly? date = null, string? reference = null, string? notes = null) =>
        new(3, 18.45m, reference.ToMaybe(), notes.ToMaybe(), date.ToMaybe(), address.ToMaybe());

    private static AddressBook Book(params string[] allowed) =>
        new([Warehouse, Site], Maybe.Some("MAG"), allowed.Length == 0 ? Maybe<EquatableArray<string>>.None : Maybe.Some<EquatableArray<string>>([.. allowed]));

    private static IEnumerable<string> Errors(CartSnapshot cart, CheckoutForm form, AddressBook book) =>
        OrderDraft.Validate(cart, form, book, Today).ShouldBeInvalid().Select(e => e.Code);

    [Fact]
    public void A_reviewed_cart_becomes_an_order_with_frozen_lines_and_the_buyers_details()
    {
        var order = OrderDraft.Validate(Cart(), Form("BUD", Today.AddDays(3), " PO-77 ", " Proszę dzwonić "), Book(), Today).ShouldBeValid();

        Assert.Equal([new OrderLine("A", "Śruba", "szt", 10, 1.5m, 23, 15m, "HURT", true)], order.Lines);
        Assert.Equal(Pln, order.Totals);
        Assert.Equal(new OrderDetails(Maybe.Some("PO-77"), Maybe.Some("Proszę dzwonić"), Maybe.Some(Today.AddDays(3)), Maybe.Some(Site)), order.Details);
    }

    [Fact]
    public void Without_a_chosen_address_the_default_one_the_buyer_may_use_is_taken()
    {
        Assert.Equal(Warehouse, OrderDraft.Validate(Cart(), Form(), Book(), Today).ShouldBeValid().Details.Address.ShouldBeSome());
        Assert.Equal(Site, OrderDraft.Validate(Cart(), Form(), Book("BUD"), Today).ShouldBeValid().Details.Address.ShouldBeSome());
        OrderDraft.Validate(Cart(), Form(), new AddressBook([], Maybe<string>.None, Maybe<EquatableArray<string>>.None), Today).ShouldBeValid().Details.Address.ShouldBeNone();
    }

    [Fact]
    public void Only_permitted_addresses_are_accepted()
    {
        Assert.Equal(["order.address-not-allowed"], Errors(Cart(), Form("MAG"), Book("BUD")));
        Assert.Equal(["order.address-not-allowed"], Errors(Cart(), Form("NONE"), Book()));
        Assert.Equal(["order.no-address-allowed"], Errors(Cart(), Form(), Book("GONE")));
    }

    [Fact]
    public void The_cart_must_be_the_one_reviewed_complete_and_orderable()
    {
        Assert.Equal(["order.cart-incomplete"], Errors(Cart(complete: false), Form(), Book()));
        Assert.Equal(["order.cannot-order"], Errors(Cart(canOrder: false), Form(), Book()));
        Assert.Equal(["order.cart-changed"], Errors(Cart(version: 4), Form(), Book()));
        Assert.Equal(["order.cart-changed"], Errors(Cart(totals: [Pln with { Gross = 18.46m }]), Form(), Book()));
        Assert.Equal(["order.mixed-currencies"], Errors(Cart(totals: [Pln, Pln with { Currency = "EUR" }]), Form(), Book()));
        Assert.Equal(["order.cart-incomplete"], Errors(Cart() with { Lines = [Line(priced: false)] }, Form(), Book()));
    }

    [Fact]
    public void Every_problem_of_the_form_is_reported_with_its_field()
    {
        var errors = OrderDraft.Validate(Cart(canOrder: false), Form("NONE", Today.AddDays(-1), new string('R', 51), new string('N', 1001)), Book(), Today).ShouldBeInvalid();

        Assert.Equal(
            [("order.cannot-order", ""), ("order.customer-reference-too-long", "customerReference"), ("order.notes-too-long", "notes"), ("order.requested-date-past", "requestedDate"), ("order.address-not-allowed", "addressCode")],
            errors.Select(e => (e.Code, e.Field.ValueOr(string.Empty))));
        Assert.Equal(["order.requested-date-too-far"], Errors(Cart(), Form(date: Today.AddDays(366)), Book()));
    }

    [Fact]
    public void Order_numbers_have_a_prefix_the_year_and_a_six_digit_sequence()
    {
        Assert.Equal("WWW/2026/000042", OrderNumbers.Format("WWW", 2026, 42));
        Assert.Equal("B2B", OrderNumbers.Prefix(" B2B ").ShouldSucceed());
        Assert.Equal("order-number.prefix", OrderNumbers.Prefix("W/W").ShouldFail().Code);
        Assert.Equal("order-number.prefix", OrderNumbers.Prefix("").ShouldFail().Code);
    }
}
