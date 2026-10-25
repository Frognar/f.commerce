using FCommerce.Ordering.Domain;
using FCommerce.SharedKernel.Collections;
using FCommerce.SharedKernel.Deciders;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Results;
using FCommerce.Testing;

namespace FCommerce.Ordering.Tests;

public sealed class OrderDeciderTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);
    private static readonly DecisionContext Context = new(Now);
    private static readonly Guid Buyer = Guid.Parse("0199f1a0-0000-7000-8000-00000000000b");

    private static readonly ValidOrder Order = new(
        [new OrderLine("A", "Śruba", "szt", 10, 1.5m, 23, 15m, "HURT", false)],
        new OrderTotals("PLN", 15m, [new VatTotal(23, 15m, 3.45m)], 3.45m, 18.45m),
        new OrderDetails(Maybe<string>.None, Maybe<string>.None, Maybe<DateOnly>.None, Maybe<DeliveryAddress>.None));

    private static readonly OrderEvent.OrderPlaced Placed =
        new("WWW/2026/000001", "K1", Buyer, Now, Order.Lines, Order.Totals, Order.Details);

    private static OrderState Given(params OrderEvent[] events) => Orders.Decider.Fold(events);

    private static EquatableArray<OrderEvent> When(OrderState state, OrderCommand command) => Orders.Decide(command, state, Context).ShouldSucceed();

    private static string Fails(OrderState state, OrderCommand command) => Orders.Decide(command, state, Context).ShouldFail().Code;

    private static OrderEvent.DocumentLinked Document(string id, LinkedDocumentKind kind) => new(id, kind, $"N/{id}", Now.AddDays(1));

    [Fact]
    public void An_order_is_placed_once_with_its_frozen_lines()
    {
        var events = When(OrderState.Initial, new OrderCommand.Place("WWW/2026/000001", "K1", Buyer, Order));
        var state = Orders.Decider.Fold(events);

        Assert.Equal([Placed], events);
        Assert.Equal(OrderStatus.Placed, state.Status);
        Assert.Equal("order.exists", Fails(state, new OrderCommand.Place("WWW/2026/000002", "K1", Buyer, Order)));
        Assert.Equal("order.not-found", Fails(OrderState.Initial, new OrderCommand.Accept("ZAM/1")));
    }

    [Fact]
    public void The_erp_accepts_once_and_the_same_confirmation_again_changes_nothing()
    {
        var accepted = Given(Placed, new OrderEvent.OrderAccepted("ZAM/1", Now));

        Assert.Equal([new OrderEvent.OrderAccepted("ZAM/1", Now)], When(Given(Placed), new OrderCommand.Accept(" ZAM/1 ")));
        Assert.Empty(When(accepted, new OrderCommand.Accept("ZAM/1")));
        Assert.Equal("order.already-accepted", Fails(accepted, new OrderCommand.Accept("ZAM/2")));
        Assert.Equal("erp-number.empty", Fails(Given(Placed), new OrderCommand.Accept(" ")));
        Assert.Equal((OrderStatus.Accepted, "ZAM/1"), (accepted.Status, accepted.ErpNumber.ShouldBeSome()));
    }

    [Fact]
    public void Only_an_order_not_yet_taken_over_is_rejected_or_cancelled_by_the_buyer()
    {
        var accepted = Given(Placed, new OrderEvent.OrderAccepted("ZAM/1", Now));
        var cancelled = Given(Placed, new OrderEvent.OrderCancelled(CancelledBy.Customer, Maybe<string>.None, Now));

        Assert.Equal([new OrderEvent.OrderRejected(Maybe.Some("Brak towaru"), Now)], When(Given(Placed), new OrderCommand.Reject(" Brak towaru ")));
        Assert.Equal([new OrderEvent.OrderCancelled(CancelledBy.Customer, Maybe<string>.None, Now)], When(Given(Placed), new OrderCommand.CancelByCustomer()));
        Assert.Equal("order.already-accepted", Fails(accepted, new OrderCommand.Reject(Maybe<string>.None)));
        Assert.Equal("order.not-cancellable", Fails(accepted, new OrderCommand.CancelByCustomer()));
        Assert.Empty(When(cancelled, new OrderCommand.CancelByCustomer()));
        Assert.Equal("order.cancelled", Fails(cancelled, new OrderCommand.Accept("ZAM/1")));
        Assert.Equal("order.reason-too-long", Fails(Given(Placed), new OrderCommand.Reject(new string('x', 501))));
    }

    [Fact]
    public void The_seller_cancels_until_the_order_is_invoiced()
    {
        var inProgress = Given(Placed, Document("WZ-1", LinkedDocumentKind.DeliveryNote));
        var invoiced = Given(Placed, Document("FV-1", LinkedDocumentKind.Invoice));

        Assert.Equal([new OrderEvent.OrderCancelled(CancelledBy.Seller, Maybe<string>.None, Now)], When(inProgress, new OrderCommand.CancelBySeller(" ")));
        Assert.Equal("order.completed", Fails(invoiced, new OrderCommand.CancelBySeller(Maybe<string>.None)));
        Assert.Equal("order.rejected", Fails(Given(Placed, new OrderEvent.OrderRejected(Maybe<string>.None, Now)), new OrderCommand.CancelBySeller(Maybe<string>.None)));
    }

    [Fact]
    public void Documents_move_the_status_and_are_linked_once()
    {
        var withNote = Given(Placed, Document("WZ-1", LinkedDocumentKind.DeliveryNote));
        var withProforma = Given(Placed, Document("PF-1", LinkedDocumentKind.Proforma));
        var completed = Given(Placed, Document("WZ-1", LinkedDocumentKind.DeliveryNote), Document("FV-1", LinkedDocumentKind.Invoice));

        Assert.Empty(When(withNote, new OrderCommand.LinkDocument("WZ-1", LinkedDocumentKind.DeliveryNote, "N/WZ-1")));
        Assert.Equal(OrderStatus.Accepted, withProforma.Status);
        Assert.Equal(OrderStatus.InProgress, withNote.Status);
        Assert.Equal(OrderStatus.Completed, completed.Status);
        Assert.Equal(
            [new OrderMilestone(OrderStatus.Placed, Now), new OrderMilestone(OrderStatus.Accepted, Now.AddDays(1)), new OrderMilestone(OrderStatus.InProgress, Now.AddDays(1)), new OrderMilestone(OrderStatus.Completed, Now.AddDays(1))],
            completed.Milestones);
    }

    [Fact]
    public void A_cancelled_order_stays_cancelled_whatever_documents_come()
    {
        var state = Given(Placed, new OrderEvent.OrderCancelled(CancelledBy.Seller, Maybe.Some("Klient zrezygnował"), Now), Document("FV-1", LinkedDocumentKind.Invoice));

        Assert.Equal(OrderStatus.Cancelled, state.Status);
    }
}
