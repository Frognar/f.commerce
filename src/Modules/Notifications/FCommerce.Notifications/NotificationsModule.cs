using FCommerce.Access.Contracts;
using FCommerce.Application.Email;
using FCommerce.Application.Messaging;
using FCommerce.Infrastructure;
using FCommerce.Notifications.Templates;
using FCommerce.Settings.Contracts;
using FCommerce.SharedKernel.Contact;
using FCommerce.SharedKernel.Optional;
using Microsoft.Extensions.DependencyInjection;

namespace FCommerce.Notifications;

public sealed class NotificationsOptions
{
    /// <summary>Public address of the storefront, used to build links in e-mails, e.g. "https://b2b.example.com".</summary>
    public required Uri PublicUrl { get; init; }

    /// <summary>Language of e-mails to the seller's own mailboxes (e.g. new registration requests).</summary>
    public string SellerCulture { get; init; } = "pl";

    /// <summary>IANA time zone of the seller: dates in e-mails.</summary>
    public string TimeZone { get; init; } = "Europe/Warsaw";
}

public static class NotificationsModule
{
    /// <summary>
    /// Requires an <see cref="IEmailSender"/> and the Settings module (the store name comes from its branding); e-mails about
    /// orders read the buyer from Access and the salesperson from Customers.
    /// </summary>
    public static IServiceCollection AddNotificationsModule(this IServiceCollection services, NotificationsOptions options)
    {
        services.AddSingleton(options);
        services.AddScoped<OrderMail>();
        services.AddIntegrationEventHandler<InvitationIssued, SendInvitation>();
        services.AddIntegrationEventHandler<PasswordResetRequested, SendPasswordReset>();
        services.AddIntegrationEventHandler<MembershipGranted, SendMembershipGranted>();
        services.AddIntegrationEventHandler<RegistrationSubmitted, NotifyOperatorsOfRegistration>();
        services.AddIntegrationEventHandler<RegistrationRejected, SendRegistrationRejected>();
        services.AddIntegrationEventHandler<Ordering.Contracts.OrderPlaced, ConfirmOrderPlaced>();
        services.AddIntegrationEventHandler<Ordering.Contracts.OrderStatusChanged, SendOrderStatusChanged>();
        return services;
    }
}

internal static class Links
{
    public static Uri To(NotificationsOptions options, string culture, string path, string token) =>
        new(options.PublicUrl, $"/{culture}/{path}?token={Uri.EscapeDataString(token)}");

    public static Uri To(NotificationsOptions options, string culture, string path) =>
        new(options.PublicUrl, $"/{culture}/{path}");

    /// <summary>Addresses in events were validated when the account was created.</summary>
    public static EmailAddress Address(string email) =>
        EmailAddress.Parse(email).Match(address => address, error => throw new InvalidOperationException(error.Message));
}

internal sealed class SendInvitation(IEmailSender sender, NotificationsOptions options, IQueryHandler<GetPlatformSettings, PlatformSettingsView> settings)
    : IIntegrationEventHandler<InvitationIssued>
{
    public async Task HandleAsync(InvitationIssued invitation, CancellationToken cancellationToken = default)
    {
        var storeName = (await settings.HandleAsync(new GetPlatformSettings(), cancellationToken)).Branding.StoreName;
        var culture = invitation.Culture;
        var forOperator = invitation.Audience == "operator";
        var email = new ActionEmail(
            EmailRenderer.Text(forOperator ? "InvitationOperatorSubject" : "InvitationCustomerSubject", culture, storeName),
            EmailRenderer.Text("Greeting", culture, invitation.Name),
            EmailRenderer.Text(forOperator ? "InvitationOperatorBody" : "InvitationCustomerBody", culture, storeName, invitation.CustomerName.OrEmpty()),
            EmailRenderer.Text("InvitationAction", culture),
            Links.To(options, culture, "invitation", invitation.Token),
            EmailRenderer.Text("InvitationExpiry", culture));
        await sender.SendAsync(EmailRenderer.Render(Links.Address(invitation.Email), invitation.Name, culture, storeName, email), cancellationToken);
    }
}

internal sealed class SendPasswordReset(IEmailSender sender, NotificationsOptions options, IQueryHandler<GetPlatformSettings, PlatformSettingsView> settings)
    : IIntegrationEventHandler<PasswordResetRequested>
{
    public async Task HandleAsync(PasswordResetRequested reset, CancellationToken cancellationToken = default)
    {
        var storeName = (await settings.HandleAsync(new GetPlatformSettings(), cancellationToken)).Branding.StoreName;
        var culture = reset.Culture;
        var email = new ActionEmail(
            EmailRenderer.Text("ResetSubject", culture, storeName),
            EmailRenderer.Text("Greeting", culture, reset.Name),
            EmailRenderer.Text("ResetBody", culture),
            EmailRenderer.Text("ResetAction", culture),
            Links.To(options, culture, "reset-password", reset.Token),
            EmailRenderer.Text("ResetExpiry", culture));
        await sender.SendAsync(EmailRenderer.Render(Links.Address(reset.Email), reset.Name, culture, storeName, email), cancellationToken);
    }
}

internal sealed class SendMembershipGranted(IEmailSender sender, NotificationsOptions options, IQueryHandler<GetPlatformSettings, PlatformSettingsView> settings)
    : IIntegrationEventHandler<MembershipGranted>
{
    public async Task HandleAsync(MembershipGranted granted, CancellationToken cancellationToken = default)
    {
        var storeName = (await settings.HandleAsync(new GetPlatformSettings(), cancellationToken)).Branding.StoreName;
        var culture = granted.Culture;
        var email = new ActionEmail(
            EmailRenderer.Text("MembershipGrantedSubject", culture, storeName, granted.CustomerName),
            EmailRenderer.Text("Greeting", culture, granted.Name),
            EmailRenderer.Text("MembershipGrantedBody", culture, storeName, granted.CustomerName),
            EmailRenderer.Text("MembershipGrantedAction", culture),
            Links.To(options, culture, "sign-in"),
            EmailRenderer.Text("MembershipGrantedNote", culture));
        await sender.SendAsync(EmailRenderer.Render(Links.Address(granted.Email), granted.Name, culture, storeName, email), cancellationToken);
    }
}

/// <summary>Tells the seller's operator mailbox (settings: operator notifications) about a new request; nothing when unset.</summary>
internal sealed class NotifyOperatorsOfRegistration(IEmailSender sender, NotificationsOptions options, IQueryHandler<GetPlatformSettings, PlatformSettingsView> settings)
    : IIntegrationEventHandler<RegistrationSubmitted>
{
    public async Task HandleAsync(RegistrationSubmitted submitted, CancellationToken cancellationToken = default)
    {
        var current = await settings.HandleAsync(new GetPlatformSettings(), cancellationToken);
        if (current.Contact.OperatorNotifications is not { } mailbox)
        {
            return;
        }

        var storeName = current.Branding.StoreName;
        var culture = options.SellerCulture;
        var email = new ActionEmail(
            EmailRenderer.Text("RegistrationSubmittedSubject", culture, submitted.CompanyName),
            EmailRenderer.Text("GreetingTeam", culture),
            EmailRenderer.Text("RegistrationSubmittedBody", culture, submitted.CompanyName, submitted.TaxId, storeName, submitted.PersonName, submitted.Email),
            EmailRenderer.Text("RegistrationSubmittedAction", culture),
            Links.To(options, culture, $"admin/registrations/{submitted.RequestId}"),
            EmailRenderer.Text("RegistrationSubmittedNote", culture));
        await sender.SendAsync(EmailRenderer.Render(Links.Address(mailbox), storeName, culture, storeName, email), cancellationToken);
    }
}

internal sealed class SendRegistrationRejected(IEmailSender sender, NotificationsOptions options, IQueryHandler<GetPlatformSettings, PlatformSettingsView> settings)
    : IIntegrationEventHandler<RegistrationRejected>
{
    public async Task HandleAsync(RegistrationRejected rejected, CancellationToken cancellationToken = default)
    {
        var storeName = (await settings.HandleAsync(new GetPlatformSettings(), cancellationToken)).Branding.StoreName;
        var culture = rejected.Culture;
        var email = new ActionEmail(
            EmailRenderer.Text("RegistrationRejectedSubject", culture, storeName),
            EmailRenderer.Text("Greeting", culture, rejected.Name),
            EmailRenderer.Text("RegistrationRejectedBody", culture, rejected.CompanyName, rejected.Reason),
            EmailRenderer.Text("RegistrationRejectedAction", culture),
            Links.To(options, culture, string.Empty),
            EmailRenderer.Text("RegistrationRejectedNote", culture));
        await sender.SendAsync(EmailRenderer.Render(Links.Address(rejected.Email), rejected.Name, culture, storeName, email), cancellationToken);
    }
}
