using System.Globalization;
using FCommerce.Access.Contracts;
using FCommerce.Application.Email;
using FCommerce.Application.Messaging;
using FCommerce.Customers.Contracts;
using FCommerce.Notifications.Templates;
using FCommerce.Ordering.Contracts;
using FCommerce.Settings.Contracts;
using FCommerce.SharedKernel.Contact;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Results;

namespace FCommerce.Notifications;

/// <summary>
/// E-mails about orders say only the number, the date and where to look: the order itself is shown on the platform to
/// people signed in with the right to see it, never sent by e-mail.
/// </summary>
internal sealed class OrderMail(
    IEmailSender sender,
    NotificationsOptions options,
    IQueryHandler<GetPlatformSettings, PlatformSettingsView> settings,
    IQueryHandler<GetUserContact, Result<UserContact, Error>> contacts)
{
    private readonly TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById(options.TimeZone);

    public NotificationsOptions Options => options;

    public async Task<string> StoreNameAsync(CancellationToken cancellationToken) =>
        (await settings.HandleAsync(new GetPlatformSettings(), cancellationToken)).Branding.StoreName;

    /// <summary>The buyer, if their account is still active.</summary>
    public async Task<Maybe<UserContact>> BuyerAsync(Guid userId, CancellationToken cancellationToken) =>
        (await contacts.HandleAsync(new GetUserContact(userId), cancellationToken)).ToMaybe();

    public string Date(DateTimeOffset moment, string culture) =>
        TimeZoneInfo.ConvertTime(moment, zone).ToString("g", CultureInfo.GetCultureInfo(culture));

    public Task SendAsync(EmailAddress to, string toName, string culture, string storeName, ActionEmail email, CancellationToken cancellationToken) =>
        sender.SendAsync(EmailRenderer.Render(to, toName, culture, storeName, email), cancellationToken);
}

/// <summary>Confirms a placed order to the buyer and tells the customer's salesperson (in the seller's language), when there is one.</summary>
internal sealed class ConfirmOrderPlaced(OrderMail mail, IQueryHandler<GetCustomer, Result<CustomerDetails, Error>> customers)
    : IIntegrationEventHandler<OrderPlaced>
{
    public async Task HandleAsync(OrderPlaced placed, CancellationToken cancellationToken = default)
    {
        var storeName = await mail.StoreNameAsync(cancellationToken);
        foreach (var buyer in (await mail.BuyerAsync(placed.PlacedBy, cancellationToken)).AsEnumerable())
        {
            var culture = buyer.Culture;
            var email = new ActionEmail(
                EmailRenderer.Text("OrderPlacedSubject", culture, placed.Number, storeName),
                EmailRenderer.Text("Greeting", culture, buyer.Name),
                EmailRenderer.Text("OrderPlacedBody", culture, placed.Number, mail.Date(placed.PlacedAt, culture)),
                EmailRenderer.Text("OrderAction", culture),
                Links.To(mail.Options, culture, $"company/orders/{placed.OrderId}"),
                EmailRenderer.Text("OrderNote", culture));
            await mail.SendAsync(Links.Address(buyer.Email), buyer.Name, culture, storeName, email, cancellationToken);
        }

        var customer = (await customers.HandleAsync(new GetCustomer(placed.CustomerCode), cancellationToken)).ToMaybe();
        var salesperson = customer.Bind(c => c.Salesperson.ToMaybe())
            .Bind(s => s.Email.ToMaybe().Bind(email => EmailAddress.Parse(email).ToMaybe()).Map(address => (s.Name, Address: address)));
        foreach (var (name, address) in salesperson.AsEnumerable())
        {
            var culture = mail.Options.SellerCulture;
            var customerName = customer.Map(c => c.Name).ValueOr(placed.CustomerCode);
            var email = new ActionEmail(
                EmailRenderer.Text("OrderForSalespersonSubject", culture, placed.Number, customerName),
                EmailRenderer.Text("Greeting", culture, name),
                EmailRenderer.Text("OrderForSalespersonBody", culture, customerName, placed.Number, mail.Date(placed.PlacedAt, culture)),
                EmailRenderer.Text("OrderAction", culture),
                Links.To(mail.Options, culture, $"admin/orders/{placed.OrderId}"),
                EmailRenderer.Text("OrderForSalespersonNote", culture));
            await mail.SendAsync(address, name, culture, storeName, email, cancellationToken);
        }
    }
}

/// <summary>Tells the buyer what the ERP decided about their order.</summary>
internal sealed class OrderDecisionMail(OrderMail mail)
{
    /// <param name="subject">The key of the subject text ({0}: the order number, {1}: the decision).</param>
    /// <param name="decision">The key of the name of the decision ({1} in the subject and the body).</param>
    public async Task SendAsync(Guid orderId, string number, Guid buyerId, string subject, string body, string decision, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var storeName = await mail.StoreNameAsync(cancellationToken);
        foreach (var buyer in (await mail.BuyerAsync(buyerId, cancellationToken)).AsEnumerable())
        {
            var culture = buyer.Culture;
            var name = EmailRenderer.Text(decision, culture);
            var email = new ActionEmail(
                EmailRenderer.Text(subject, culture, number, name),
                EmailRenderer.Text("Greeting", culture, buyer.Name),
                EmailRenderer.Text(body, culture, number, name, mail.Date(at, culture)),
                EmailRenderer.Text("OrderAction", culture),
                Links.To(mail.Options, culture, $"company/orders/{orderId}"),
                EmailRenderer.Text("OrderNote", culture));
            await mail.SendAsync(Links.Address(buyer.Email), buyer.Name, culture, storeName, email, cancellationToken);
        }
    }

    public Task StatusAsync(Guid orderId, string number, Guid buyerId, string status, DateTimeOffset at, CancellationToken cancellationToken) =>
        SendAsync(orderId, number, buyerId, "OrderStatusSubject", "OrderStatusBody", $"OrderStatus.{status}", at, cancellationToken);
}

/// <summary>The ERP took the order over (by a confirmation or its first document).</summary>
internal sealed class SendOrderAccepted(OrderDecisionMail mail) : IIntegrationEventHandler<OrderAccepted>
{
    public Task HandleAsync(OrderAccepted accepted, CancellationToken cancellationToken = default) =>
        mail.StatusAsync(accepted.OrderId, accepted.Number, accepted.PlacedBy, OrderStatuses.Accepted, accepted.At, cancellationToken);
}

internal sealed class SendOrderRejected(OrderDecisionMail mail) : IIntegrationEventHandler<OrderRejected>
{
    public Task HandleAsync(OrderRejected rejected, CancellationToken cancellationToken = default) =>
        mail.StatusAsync(rejected.OrderId, rejected.Number, rejected.PlacedBy, OrderStatuses.Rejected, rejected.At, cancellationToken);
}

/// <summary>The ERP cancelled the order: of its own accord, or confirming the buyer's request.</summary>
internal sealed class SendOrderCancelled(OrderDecisionMail mail) : IIntegrationEventHandler<OrderCancelled>
{
    public Task HandleAsync(OrderCancelled cancelled, CancellationToken cancellationToken = default) =>
        mail.StatusAsync(cancelled.OrderId, cancelled.Number, cancelled.PlacedBy, OrderStatuses.Cancelled, cancelled.At, cancellationToken);
}

/// <summary>The ERP keeps an order the buyer asked to cancel.</summary>
internal sealed class SendOrderCancellationRefused(OrderDecisionMail mail) : IIntegrationEventHandler<OrderCancellationRefused>
{
    public Task HandleAsync(OrderCancellationRefused refused, CancellationToken cancellationToken = default) =>
        mail.SendAsync(refused.OrderId, refused.Number, refused.PlacedBy, "OrderCancellationRefusedSubject", "OrderCancellationRefusedBody", "OrderCancellationRefused", refused.At, cancellationToken);
}
