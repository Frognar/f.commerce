using FCommerce.Access.Contracts;
using FCommerce.Application.Email;
using FCommerce.Application.Messaging;
using FCommerce.Customers.Contracts;
using FCommerce.Ordering.Contracts;
using FCommerce.Settings.Contracts;
using FCommerce.SharedKernel.Results;

namespace FCommerce.Notifications.Tests;

public sealed class OrderEmailTests
{
    private static readonly Guid OrderId = Guid.Parse("0199f1a0-0000-7000-8000-0000000000aa");
    private static readonly Guid Buyer = Guid.Parse("0199f1a0-0000-7000-8000-0000000000bb");
    private static readonly DateTimeOffset PlacedAt = new(2026, 10, 25, 8, 30, 0, TimeSpan.Zero);

    private static readonly NotificationsOptions Options = new() { PublicUrl = new Uri("https://b2b.example.com"), SellerCulture = "pl", TimeZone = "Europe/Warsaw" };

    private static readonly OrderPlaced Placed = new(OrderId, "WWW/2026/000042", "K1", Buyer, PlacedAt, [new OrderItemData("SR-M8-SEKRET", 12)]);

    private sealed class Recorder : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = [];

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class Settings : IQueryHandler<GetPlatformSettings, PlatformSettingsView>
    {
        public Task<PlatformSettingsView> HandleAsync(GetPlatformSettings query, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PlatformSettingsView(1, "Public", [], new BrandingView("Hurtownia", "#1f5eff", null), new FCommerce.Settings.Contracts.ContactView(null, null, null), null, "buyer", "Levels", 10));
    }

    private sealed class Contacts(string culture) : IQueryHandler<GetUserContact, Result<UserContact, Error>>
    {
        public Task<Result<UserContact, Error>> HandleAsync(GetUserContact query, CancellationToken cancellationToken = default) =>
            Task.FromResult(query.UserId == Buyer
                ? Result.Ok<UserContact, Error>(new UserContact(Buyer, "piotr@firma.pl", "Piotr", culture))
                : Error.NotFound("access.user-not-found", "No such user."));
    }

    private sealed class Customers(string? salespersonEmail) : IQueryHandler<GetCustomer, Result<CustomerDetails, Error>>
    {
        public Task<Result<CustomerDetails, Error>> HandleAsync(GetCustomer query, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Ok<CustomerDetails, Error>(new CustomerDetails(
                "K1", "Budmax", null, new AddressView("ul. Długa 1", "00-950", "Warszawa", "PL"), [], [],
                salespersonEmail is null ? null : new SalespersonView("Anna", salespersonEmail, null), null, null, "PLN", false, true, PlacedAt)));
    }

    private static OrderMail Mail(Recorder recorder, string culture = "pl") => new(recorder, Options, new Settings(), new Contacts(culture));

    [Fact]
    public async Task The_buyer_gets_the_number_the_date_and_a_link_but_not_the_order()
    {
        var recorder = new Recorder();

        await new ConfirmOrderPlaced(Mail(recorder), new Customers(null)).HandleAsync(Placed, TestContext.Current.CancellationToken);

        var email = Assert.Single(recorder.Sent);
        Assert.Equal(("piotr@firma.pl", "Zamówienie WWW/2026/000042 w Hurtownia zostało złożone"), (email.To.Value, email.Subject));
        Assert.Contains("25.10.2026 09:30", email.Text, StringComparison.Ordinal);
        Assert.Contains($"https://b2b.example.com/pl/company/orders/{OrderId}", email.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("SR-M8-SEKRET", email.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("SR-M8-SEKRET", email.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_salesperson_gets_a_link_to_the_back_office_in_the_sellers_language()
    {
        var recorder = new Recorder();

        await new ConfirmOrderPlaced(Mail(recorder, culture: "en"), new Customers("anna@seller.pl")).HandleAsync(Placed, TestContext.Current.CancellationToken);

        Assert.Equal(["piotr@firma.pl", "anna@seller.pl"], recorder.Sent.Select(e => e.To.Value));
        Assert.StartsWith("Order WWW/2026/000042", recorder.Sent[0].Subject, StringComparison.Ordinal);
        var salesperson = recorder.Sent[1];
        Assert.Equal("Nowe zamówienie WWW/2026/000042 od Budmax", salesperson.Subject);
        Assert.Contains($"https://b2b.example.com/pl/admin/orders/{OrderId}", salesperson.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_buyer_hears_when_the_seller_accepts_rejects_or_cancels_but_not_about_their_own_cancellation()
    {
        var recorder = new Recorder();
        var handler = new SendOrderStatusChanged(Mail(recorder));
        var ct = TestContext.Current.CancellationToken;

        await handler.HandleAsync(new OrderStatusChanged(OrderId, Placed.Number, "K1", Buyer, OrderStatuses.Accepted, null, null, PlacedAt), ct);
        await handler.HandleAsync(new OrderStatusChanged(OrderId, Placed.Number, "K1", Buyer, OrderStatuses.Cancelled, "customer", null, PlacedAt), ct);
        await handler.HandleAsync(new OrderStatusChanged(OrderId, Placed.Number, "K1", Guid.NewGuid(), OrderStatuses.Rejected, null, "Brak", PlacedAt), ct);

        var email = Assert.Single(recorder.Sent);
        Assert.Equal("Zamówienie WWW/2026/000042: przyjęte", email.Subject);
        Assert.Contains($"https://b2b.example.com/pl/company/orders/{OrderId}", email.Text, StringComparison.Ordinal);
    }
}
