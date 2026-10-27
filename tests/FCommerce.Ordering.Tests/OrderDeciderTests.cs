using FCommerce.Ordering.Domain;
using FCommerce.SharedKernel.Collections;
using FCommerce.SharedKernel.Deciders;
using FCommerce.SharedKernel.Optional;
using FCommerce.Testing;
using static FCommerce.Ordering.Tests.OrderValues;

namespace FCommerce.Ordering.Tests;

public sealed class OrderDeciderTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);
    private static readonly DecisionContext Context = new(Now);
    private static readonly Guid Buyer = Guid.Parse("0199f1a0-0000-7000-8000-00000000000b");
    private static readonly ValidOrder Valid = Order(Priced(Line()));

    private static readonly OrderCommand.Place PlaceIt = new(Number("WWW/2026/000001"), CustomerCode.Parse("K1").ShouldSucceed(), Buyer, Valid);
    private static readonly OrderCommand Accept = new OrderCommand.Accept(Erp("ZAM/1"));
    private static readonly OrderCommand Reject = new OrderCommand.Reject(Because("Brak towaru"));
    private static readonly OrderCommand Request = new OrderCommand.RequestCancellation(Buyer);
    private static readonly OrderCommand Cancel = new OrderCommand.Cancel(Because("Klient zrezygnował"));
    private static readonly OrderCommand Refuse = new OrderCommand.RefuseCancellation(Because("Towar wysłany"));

    private static OrderCommand.LinkDocument Link(string id, LinkedDocumentKind kind) => new OrderCommand.LinkDocument(Document(id), kind, DocumentNo($"N/{id}"));

    /// <summary>The state after running the commands one after another (each must succeed).</summary>
    private static OrderState After(params OrderCommand[] commands) =>
        commands.Aggregate(OrderState.Initial, (state, command) => Orders.Decider.Fold(state, Orders.Decide(command, state, Context).ShouldSucceed()));

    private static OrderState.Placed Placed(params OrderCommand[] commands) => Assert.IsType<OrderState.Placed>(After([PlaceIt, .. commands]));

    private static EquatableArray<OrderEvent> When(OrderState state, OrderCommand command) => Orders.Decide(command, state, Context).ShouldSucceed();

    private static string Fails(OrderState state, OrderCommand command) => Orders.Decide(command, state, Context).ShouldFail().Code;

    private static OrderMilestoneKind[] History(OrderState.Placed state) => [.. state.History.Select(m => m.Kind)];

    [Fact]
    public void An_order_is_placed_once_with_its_frozen_lines_and_waits_for_the_erp()
    {
        var events = When(OrderState.Initial, PlaceIt);
        var state = Placed();

        var placed = Assert.IsType<OrderEvent.OrderPlaced>(Assert.Single(events));
        Assert.Equal((Valid.Lines, Now, 18.45m), (placed.Lines, placed.PlacedAt, placed.Totals.Gross));
        Assert.Equal(new OrderProgress.AwaitingErp(Maybe<CancellationRequest>.None), state.Progress);
        Assert.Equal("order.exists", Fails(state, PlaceIt));
        Assert.Equal("order.not-found", Fails(OrderState.Initial, Accept));
    }

    [Fact]
    public void The_erp_accepts_once_and_the_same_confirmation_again_changes_nothing()
    {
        var accepted = Placed(Accept);

        Assert.Empty(When(accepted, Accept));
        Assert.Equal("order.already-accepted", Fails(accepted, new OrderCommand.Accept(Erp("ZAM/2"))));
        Assert.Equal((OrderStatus.Accepted, "ZAM/1"), (accepted.Status, accepted.ErpNumber.ShouldBeSome().Value));
    }

    [Fact]
    public void Only_an_order_the_erp_has_not_taken_over_is_rejected()
    {
        var rejected = Placed(Reject);

        Assert.Equal((OrderStatus.Rejected, "Brak towaru"), (rejected.Status, Assert.IsType<OrderProgress.Rejected>(rejected.Progress).Reason.ShouldBeSome().Value));
        Assert.Empty(When(rejected, Reject));
        Assert.Equal("order.already-accepted", Fails(Placed(Accept), Reject));
        Assert.Equal("order.cancelled", Fails(Placed(Cancel), Reject));
        Assert.Equal("order.rejected", Fails(rejected, Accept));
    }

    [Fact]
    public void The_buyer_only_asks_to_cancel_and_the_erp_decides()
    {
        var requested = Placed(Accept, Request);

        Assert.Equal((OrderStatus.Accepted, Buyer), (requested.Status, requested.Progress.CancellationRequest.ShouldBeSome().By));
        Assert.Empty(When(requested, Request));

        var cancelled = Assert.IsType<OrderProgress.Cancelled>(Placed(Accept, Request, Cancel).Progress);
        Assert.True(cancelled.OnRequest);
        Assert.Equal("ZAM/1", cancelled.Before.ShouldBeSome().ErpNumber.ShouldBeSome().Value);

        var kept = Placed(Accept, Request, Refuse);
        Assert.Equal((OrderStatus.Accepted, Maybe<CancellationRequest>.None), (kept.Status, kept.Progress.CancellationRequest));
        Assert.Equal("order.no-cancellation-request", Fails(kept, Refuse));
        Assert.False(Assert.IsType<OrderProgress.Cancelled>(Placed(Cancel).Progress).OnRequest);
    }

    [Theory]
    [InlineData("placed", true)]
    [InlineData("accepted", true)]
    [InlineData("in-progress", true)]
    [InlineData("completed", false)]
    [InlineData("rejected", false)]
    public void Cancelling_is_possible_until_the_order_is_invoiced_or_decided(string step, bool cancellable)
    {
        var state = step switch
        {
            "placed" => Placed(),
            "accepted" => Placed(Accept),
            "in-progress" => Placed(Link("WZ-1", LinkedDocumentKind.DeliveryNote)),
            "completed" => Placed(Link("FV-1", LinkedDocumentKind.Invoice)),
            _ => Placed(Reject),
        };

        Assert.Equal(cancellable, Orders.Decide(Request, state, Context).Match(_ => true, _ => false));
        Assert.Equal(cancellable, Orders.Decide(Cancel, state, Context).Match(_ => true, _ => false));
        Assert.Equal(cancellable, state.Progress.Cancellable);
    }

    [Fact]
    public void Documents_take_the_order_over_dispatch_and_complete_it_and_are_linked_once()
    {
        var withProforma = Placed(Link("PF-1", LinkedDocumentKind.Proforma));
        var dispatched = Placed(Link("WZ-1", LinkedDocumentKind.DeliveryNote));
        var completed = Placed(Link("WZ-1", LinkedDocumentKind.DeliveryNote), Accept, Link("FV-1", LinkedDocumentKind.Invoice), Link("WZ-2", LinkedDocumentKind.DeliveryNote));

        Assert.Equal(OrderStatus.Accepted, withProforma.Status);
        Assert.Equal(OrderStatus.InProgress, dispatched.Status);
        Assert.Empty(When(dispatched, Link("WZ-1", LinkedDocumentKind.DeliveryNote)));
        Assert.Equal((OrderStatus.Completed, "ZAM/1", 3), (completed.Status, completed.ErpNumber.ShouldBeSome().Value, completed.Documents.Count));
        Assert.Equal([OrderMilestoneKind.Placed, OrderMilestoneKind.Accepted, OrderMilestoneKind.Dispatched, OrderMilestoneKind.Invoiced], History(completed));
        Assert.IsType<OrderEvent.ErpNumberAssigned>(Assert.Single(When(dispatched, Accept)));
    }

    [Fact]
    public void A_document_of_the_erp_after_a_cancellation_or_rejection_wins()
    {
        var shipped = Placed(Accept, Cancel, Link("WZ-1", LinkedDocumentKind.DeliveryNote));
        var invoiced = Placed(Reject, Link("FV-1", LinkedDocumentKind.Invoice));
        var proforma = Placed(Cancel, Link("PF-1", LinkedDocumentKind.Proforma));

        Assert.Equal((OrderStatus.InProgress, "ZAM/1"), (shipped.Status, shipped.ErpNumber.ShouldBeSome().Value));
        Assert.Equal([OrderMilestoneKind.Placed, OrderMilestoneKind.Accepted, OrderMilestoneKind.Cancelled, OrderMilestoneKind.Dispatched], History(shipped));
        Assert.Equal([OrderMilestoneKind.Placed, OrderMilestoneKind.Rejected, OrderMilestoneKind.Accepted, OrderMilestoneKind.Invoiced], History(invoiced));
        Assert.Equal(OrderStatus.Cancelled, proforma.Status);
    }

    [Fact]
    public void Folding_any_sequence_of_decided_commands_gives_a_state_whose_parts_agree()
    {
        OrderCommand[] commands = [Accept, Reject, Request, Cancel, Refuse, Link("WZ-1", LinkedDocumentKind.DeliveryNote), Link("FV-1", LinkedDocumentKind.Invoice), Link("PF-1", LinkedDocumentKind.Proforma)];
        var random = new Random(7);

        for (var run = 0; run < 500; run++)
        {
            OrderState state = Placed();
            for (var step = 0; step < 6; step++)
            {
                var command = commands[random.Next(commands.Length)];
                state = Orders.Decide(command, state, Context).Match(events => Orders.Decider.Fold(state, events), _ => state);
            }

            var placed = Assert.IsType<OrderState.Placed>(state);
            Assert.Equal(OrderMilestoneKind.Placed, placed.History[0].Kind);
            Assert.True(placed.Progress.Cancellable || placed.Progress.CancellationRequest.Match(_ => false, () => true));
            Assert.True(placed.Status is not (OrderStatus.InProgress or OrderStatus.Completed) || placed.Progress.Acceptance.Match(_ => true, () => false));
            Assert.Equal(placed.Documents.Any(d => d.Kind == LinkedDocumentKind.Invoice), placed.Status == OrderStatus.Completed);
        }
    }
}
