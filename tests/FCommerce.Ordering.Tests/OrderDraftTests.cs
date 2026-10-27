using FCommerce.Ordering.Domain;
using FCommerce.SharedKernel.Optional;
using FCommerce.Testing;
using static FCommerce.Ordering.Tests.OrderValues;

namespace FCommerce.Ordering.Tests;

public sealed class OrderDraftTests
{
    private static readonly DeliveryAddress Warehouse = Address("MAG", "Magazyn");
    private static readonly DeliveryAddress Site = Address("BUD");

    private static AddressBook Book(params string[] allowed) =>
        AddressBook.Of([(Warehouse, true), (Site, false)], AddressScope.Of(allowed.Select(code => AddressCode.Parse(code).ShouldSucceed())));

    private static IEnumerable<string> Errors(CartSnapshot cart, CheckoutForm form, AddressBook book) =>
        OrderDraft.Validate(cart, form, book, Today).ShouldBeInvalid().Select(e => e.Code);

    [Fact]
    public void A_reviewed_cart_becomes_an_order_with_frozen_lines_and_the_buyers_details()
    {
        var line = Line(backorder: true);

        var order = OrderDraft.Validate(Cart(lines: Priced(line)), Form(address: "BUD", date: Today.AddDays(3), reference: " PO-77 ", notes: " Proszę dzwonić "), Book(), Today).ShouldBeValid();

        Assert.Equal([line], order.Lines);
        Assert.Equal((15m, 3.45m, 18.45m), (order.Totals.Net, order.Totals.Vat, order.Totals.Gross));
        Assert.Equal(("PO-77", "Proszę dzwonić"), (order.Details.CustomerReference.ShouldBeSome().Value, order.Details.Notes.ShouldBeSome().Value));
        Assert.Equal((Today.AddDays(3), Site), (order.Details.RequestedDate.ShouldBeSome(), order.Details.Address.ShouldBeSome()));
    }

    [Fact]
    public void Totals_follow_from_the_lines_with_vat_per_rate_as_an_invoice_computes_it()
    {
        var totals = OrderTotals.Of(Values.Currency("PLN"), [Line("A", 3, 0.335m, 23), Line("B", 1, 10m, 8), Line("C", 1, 0.01m, 23)]);

        Assert.Equal([(8m, 10m, 0.8m), (23m, 1.02m, 0.23m)], totals.Rates.Select(r => (r.Rate.Percent, r.Net, r.Vat)));
        Assert.Equal((11.02m, 1.03m, 12.05m), (totals.Net, totals.Vat, totals.Gross));
        Assert.Equal(1.01m, Line("A", 3, 0.335m).NetAmount);
    }

    [Fact]
    public void Without_a_chosen_address_the_default_one_the_buyer_may_use_is_taken()
    {
        Assert.Equal(Warehouse, OrderDraft.Validate(Cart(), Form(), Book(), Today).ShouldBeValid().Details.Address.ShouldBeSome());
        Assert.Equal(Site, OrderDraft.Validate(Cart(), Form(), Book("BUD"), Today).ShouldBeValid().Details.Address.ShouldBeSome());
        OrderDraft.Validate(Cart(), Form(), AddressBook.Empty, Today).ShouldBeValid().Details.Address.ShouldBeNone();
    }

    [Fact]
    public void An_address_book_has_unique_codes_and_a_default_among_them()
    {
        var book = AddressBook.Of([(Site, false), (Address("BUD", "Inna"), true), (Warehouse, true)], AddressScope.All);

        Assert.Equal([Site, Warehouse], book.Addresses);
        Assert.Equal("MAG", book.DefaultCode.ShouldBeSome().Value);
        Assert.Equal(AddressScope.All, AddressScope.Of([]));
    }

    [Fact]
    public void Only_permitted_addresses_are_accepted()
    {
        Assert.Equal(["order.address-not-allowed"], Errors(Cart(), Form(address: "MAG"), Book("BUD")));
        Assert.Equal(["order.address-not-allowed"], Errors(Cart(), Form(address: "NONE"), Book()));
        Assert.Equal(["order.no-address-allowed"], Errors(Cart(), Form(), Book("GONE")));
    }

    [Fact]
    public void The_cart_must_be_the_one_reviewed_complete_priced_orderable_and_in_one_currency()
    {
        Assert.Equal(["order.cart-incomplete"], Errors(Cart(CartReadiness.Incomplete), Form(), Book()));
        Assert.Equal(["order.cannot-order"], Errors(Cart(CartReadiness.NotOrderable), Form(), Book()));
        Assert.Equal(["order.cannot-order"], Errors(Cart(CartReadiness.NotOrderable, lines: Unpriced()), Form(), Book()));
        Assert.Equal(["order.cart-changed"], Errors(Cart(version: 4), Form(), Book()));
        Assert.Equal(["order.cart-changed"], Errors(Cart(), Form(gross: 18.46m), Book()));
        Assert.Equal(["order.mixed-currencies"], Errors(Cart(lines: [Priced(Line("A")), Priced(Line("B"), "EUR")]), Form(), Book()));
        Assert.Equal(["order.cart-incomplete"], Errors(Cart(lines: [Priced(Line()), Unpriced("B")]), Form(), Book()));
    }

    [Fact]
    public void Every_problem_of_the_form_is_reported_with_its_field()
    {
        var errors = OrderDraft.Validate(Cart(CartReadiness.NotOrderable), Form(address: "NONE", date: Today.AddDays(-1), reference: new string('R', 51), notes: new string('N', 1001)), Book(), Today).ShouldBeInvalid();

        Assert.Equal(
            [("order.cannot-order", ""), ("order.customer-reference-too-long", "customerReference"), ("order.notes-too-long", "notes"), ("order.requested-date-past", "requestedDate"), ("order.address-not-allowed", "addressCode")],
            errors.Select(e => (e.Code, e.Field.ValueOr(string.Empty))));
        Assert.Equal(["order.requested-date-too-far"], Errors(Cart(), Form(date: Today.AddDays(366)), Book()));
    }

    [Fact]
    public void Order_numbers_have_a_prefix_the_year_and_a_six_digit_sequence()
    {
        Assert.Equal("WWW/2026/000042", OrderNumbers.Format(OrderNumberPrefix.Parse(" WWW ").ShouldSucceed(), 2026, 42).Value);
        Assert.Equal("order-number.prefix", OrderNumberPrefix.Parse("W/W").ShouldFail().Code);
        Assert.Equal("order-number.prefix", OrderNumberPrefix.Parse("").ShouldFail().Code);
    }
}
