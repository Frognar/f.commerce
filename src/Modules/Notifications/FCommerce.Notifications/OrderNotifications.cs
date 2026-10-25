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

/// <summary>Tells the buyer that the seller accepted, rejected or cancelled their order (not about their own cancellation).</summary>
internal sealed class SendOrderStatusChanged(OrderMail mail) : IIntegrationEventHandler<OrderStatusChanged>
{
    public async Task HandleAsync(OrderStatusChanged changed, CancellationToken cancellationToken = default)
    {
        if (changed.CancelledBy == "customer")
        {
            return;
        }

        var storeName = await mail.StoreNameAsync(cancellationToken);
        foreach (var buyer in (await mail.BuyerAsync(changed.PlacedBy, cancellationToken)).AsEnumerable())
        {
            var culture = buyer.Culture;
            var status = EmailRenderer.Text($"OrderStatus.{changed.Status}", culture);
            var email = new ActionEmail(
                EmailRenderer.Text("OrderStatusSubject", culture, changed.Number, status),
                EmailRenderer.Text("Greeting", culture, buyer.Name),
                EmailRenderer.Text("OrderStatusBody", culture, changed.Number, status, mail.Date(changed.At, culture)),
                EmailRenderer.Text("OrderAction", culture),
                Links.To(mail.Options, culture, $"company/orders/{changed.OrderId}"),
                EmailRenderer.Text("OrderNote", culture));
            await mail.SendAsync(Links.Address(buyer.Email), buyer.Name, culture, storeName, email, cancellationToken);
        }
    }
}
