using FCommerce.Ordering.Domain;
using FCommerce.SharedKernel.Contact;
using FCommerce.SharedKernel.Optional;
using FCommerce.Testing;

namespace FCommerce.Ordering.Tests;

/// <summary>Domain values for the tests, made through their parsers (as the shell makes them).</summary>
internal static class OrderValues
{
    public static readonly DateOnly Today = new(2026, 10, 25);

    public static OrderLine Line(string sku = "A", decimal quantity = 10, decimal price = 1.5m, decimal vat = 23, bool backorder = false) =>
        new(
            Sku.Parse(sku).ShouldSucceed(),
            LineName.Parse("Śruba").ShouldSucceed(),
            UnitCode.Parse("szt").ShouldSucceed(),
            OrderQuantity.Parse(quantity).ShouldSucceed(),
            NetPrice.Parse(price).ShouldSucceed(),
            VatRate.Parse(vat).ShouldSucceed(),
            PriceListCode.Parse("HURT").ShouldSucceed(),
            backorder);

    public static CheckoutLine Priced(OrderLine line, string currency = "PLN") => new CheckoutLine.Priced(Values.Currency(currency), line);

    public static CheckoutLine Unpriced(string sku = "A") =>
        new CheckoutLine.Unpriced(Sku.Parse(sku).ShouldSucceed(), Maybe<LineName>.None, Maybe<UnitCode>.None, OrderQuantity.Parse(1).ShouldSucceed());

    public static CartSnapshot Cart(CartReadiness readiness = CartReadiness.Orderable, long version = 3, params CheckoutLine[] lines) =>
        new(CartVersion.Parse(version).ShouldSucceed(), readiness, [.. lines.Length == 0 ? [Priced(Line())] : lines]);

    public static CheckoutForm Form(decimal gross = 18.45m, string? address = null, DateOnly? date = null, string? reference = null, string? notes = null) =>
        new(3, gross, reference.ToMaybe(), notes.ToMaybe(), date.ToMaybe(), address.ToMaybe());

    public static DeliveryAddress Address(string code, string? name = null) =>
        new(
            AddressCode.Parse(code).ShouldSucceed(),
            name.ToMaybe().Map(n => AddressText.Parse(n).ShouldSucceed()),
            AddressText.Parse("ul. Długa 1").ShouldSucceed(),
            PostalCode.Parse("00-950", CountryCode.Parse("PL").ShouldSucceed()).ShouldSucceed(),
            AddressText.Parse("Warszawa").ShouldSucceed(),
            CountryCode.Parse("PL").ShouldSucceed());

    public static ValidOrder Order(params CheckoutLine[] lines) =>
        OrderDraft.Validate(Cart(lines: lines), Form(gross: Cart(lines: lines).Total.ShouldBeSome().Gross), AddressBook.Empty, Today).ShouldBeValid();

    public static OrderNumber Number(string number) => OrderNumber.Parse(number).ShouldSucceed();

    public static ErpOrderNumber Erp(string number) => ErpOrderNumber.Parse(number).ShouldSucceed();

    public static Maybe<Reason> Because(string text) => Reason.ParseOptional(text).ShouldSucceed();

    public static DocumentId Document(string id) => DocumentId.Parse(id).ShouldSucceed();

    public static DocumentNumber DocumentNo(string number) => DocumentNumber.Parse(number).ShouldSucceed();
}
