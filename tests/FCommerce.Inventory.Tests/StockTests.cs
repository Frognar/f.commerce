using FCommerce.Inventory.Domain;
using FCommerce.SharedKernel.Imports;
using FCommerce.SharedKernel.Optional;
using FCommerce.Testing;

namespace FCommerce.Inventory.Tests;

public sealed class StockTests
{
    private static readonly DateOnly Today = new(2026, 10, 24);

    private static UnvalidatedStock Input(decimal quantity = 12.5m) => new(" SR-M8 ", 7, quantity, Today.AddDays(3), 100m);

    private static Amount A(decimal quantity) => Amount.Parse(quantity).ShouldSucceed();

    private static Delivery Delivery(int inDays, decimal? quantity = null) =>
        new(Today.AddDays(inDays), quantity is { } q ? A(q) : Maybe<Amount>.None);

    /// <summary>A figure as the domain makes it from stored stock.</summary>
    private static StockFigure Figure(decimal quantity, Delivery? next = null) =>
        Stock.Figure(new StockContent(StockQuantity.Parse(quantity).ShouldSucceed(), next is null ? Maybe<Delivery>.None : next), Today);

    private static (decimal Quantity, Maybe<Delivery> Delivery) Of(StockFigure figure) => (figure.Quantity.Value, figure.NextDelivery);

    [Fact]
    public void Valid_stock_is_normalized()
    {
        var entry = Stock.Validate(Input()).ShouldBeValid();

        Assert.Equal(("SR-M8", 7L, 12.5m), (entry.Sku.Value, entry.Version.Value, entry.Content.Quantity.Value));
        Assert.Equal(Delivery(3, 100m), entry.Content.NextDelivery.ShouldBeSome());
    }

    [Fact]
    public void Negative_quantities_from_the_erp_are_accepted()
    {
        Assert.Equal(-3m, Stock.Validate(Input(-3m)).ShouldBeValid().Content.Quantity.Value);
    }

    [Fact]
    public void Stock_reports_every_invalid_field()
    {
        var input = new UnvalidatedStock("", -1, 1.0001m, default, 5m);

        var errors = Stock.Validate(input).ShouldBeInvalid();

        Assert.Equal(["sku", "version", "quantity", "nextDeliveryDate"], errors.Select(e => e.Field.ValueOr("")));
        Assert.Equal("next-delivery.quantity-without-date", errors[3].Code);
    }

    [Fact]
    public void A_delivery_brings_more_than_nothing()
    {
        var errors = Stock.Validate(Input() with { NextDeliveryQuantity = 0m }).ShouldBeInvalid();

        Assert.Equal(("next-delivery.quantity-not-positive", "nextDeliveryQuantity"), (Assert.Single(errors).Code, errors[0].Field.ValueOr("")));
    }

    [Fact]
    public void A_warehouse_is_validated()
    {
        var warehouse = Stock.Validate(new UnvalidatedWarehouse(" WRO ", 1, "Wrocław", Visible: true, SortOrder: 2, Active: true)).ShouldBeValid();
        var invalid = Stock.Validate(new UnvalidatedWarehouse("", 1, " ", true, 0, true)).ShouldBeInvalid();

        Assert.Equal(("WRO", "Wrocław", 2, WarehouseVisibility.Public), (warehouse.Code.Value, warehouse.Content.Name.Value, warehouse.Content.SortOrder, warehouse.Content.Visibility));
        Assert.Equal(["code", "name"], invalid.Select(e => e.Field.ValueOr("")));
    }

    [Theory]
    [InlineData(true, true, WarehouseVisibility.Public)]
    [InlineData(false, true, WarehouseVisibility.Internal)]
    [InlineData(true, false, WarehouseVisibility.Withdrawn)]
    [InlineData(false, false, WarehouseVisibility.Withdrawn)]
    public void A_withdrawn_warehouse_is_withdrawn_shown_or_not(bool visible, bool active, WarehouseVisibility expected)
    {
        Assert.Equal(expected, Stock.VisibilityOf(visible, active));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void A_figure_shows_no_negative_stock_and_keeps_deliveries_from_today_on(int inDays)
    {
        var figure = Figure(-5m, Delivery(inDays, 10m));

        Assert.Equal(0m, figure.Quantity.Value);
        Assert.Equal(inDays >= 0, figure.NextDelivery.Exists(d => d.Date == Today.AddDays(inDays)));
    }

    [Fact]
    public void The_total_sums_warehouses_and_takes_the_earliest_delivery()
    {
        var total = Stock.Total([Figure(5m, Delivery(7, 10m)), Figure(2.5m, Delivery(3, 20m)), Figure(0m, Delivery(3, 5m)), Figure(1m)]);

        Assert.Equal((8.5m, Maybe.Some(Delivery(3, 25m))), Of(total));
    }

    [Fact]
    public void The_delivery_quantity_of_the_total_is_known_only_when_every_delivery_of_that_day_has_it()
    {
        Assert.Equal((0m, Maybe.Some(Delivery(3))), Of(Stock.Total([Figure(0m, Delivery(3, 20m)), Figure(0m, Delivery(3))])));
        Assert.Equal(StockFigure.Empty, Stock.Total([]));
    }

    [Theory]
    [InlineData(0, 10, StockStatus.None)]
    [InlineData(0.001, 10, StockStatus.Low)]
    [InlineData(10, 10, StockStatus.Low)]
    [InlineData(10.001, 10, StockStatus.Available)]
    [InlineData(0, 0, StockStatus.None)]
    [InlineData(0.001, 0, StockStatus.Available)]
    public void Status_is_none_at_zero_low_up_to_the_threshold_and_available_above(decimal quantity, decimal threshold, StockStatus status)
    {
        Assert.Equal(status, Stock.StatusOf(A(quantity), A(threshold)));
    }

    [Fact]
    public void Levels_leave_out_every_quantity_and_the_status_follows_the_quantity()
    {
        var figure = Figure(4m, Delivery(2, 30m));

        var levels = Assert.IsType<StockPresentation.Levels>(Stock.Present(figure, new StockPolicy(StockDisplayMode.Levels, A(10m))));
        var exact = Assert.IsType<StockPresentation.Exact>(Stock.Present(figure, new StockPolicy(StockDisplayMode.Exact, A(10m))));

        Assert.Equal((StockStatus.Low, Today.AddDays(2)), (levels.Status, levels.NextDelivery.ShouldBeSome()));
        Assert.Equal((StockStatus.Low, 4m, Delivery(2, 30m)), (exact.Status, exact.Quantity.Value, exact.NextDelivery.ShouldBeSome()));
    }

    [Fact]
    public void Reconciling_stock_follows_the_import_pattern()
    {
        var stored = Stock.Validate(Input()).ShouldBeValid();

        Assert.IsType<Change<StockEntry>.Stale>(Stock.Reconcile(stored, stored));
        Assert.IsType<Change<StockEntry>.Unchanged>(Stock.Reconcile(stored, Stock.Validate(Input() with { Version = 8 }).ShouldBeValid()));
        Assert.IsType<Change<StockEntry>.Updated>(Stock.Reconcile(stored, Stock.Validate(Input(0m) with { Version = 8 }).ShouldBeValid()));
    }
}
