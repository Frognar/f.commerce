using FCommerce.Access.Contracts;
using FCommerce.Application.Email;
using FCommerce.Application.Messaging;
using FCommerce.Infrastructure;
using FCommerce.Notifications.Templates;
using FCommerce.Settings.Contracts;
using FCommerce.SharedKernel.Contact;
using Microsoft.Extensions.DependencyInjection;

namespace FCommerce.Notifications;

public sealed class NotificationsOptions
{
    /// <summary>Public address of the storefront, used to build links in e-mails, e.g. "https://b2b.example.com".</summary>
    public required Uri PublicUrl { get; init; }
}

public static class NotificationsModule
{
    /// <summary>Requires an <see cref="IEmailSender"/> and the Settings module (the store name comes from its branding).</summary>
    public static IServiceCollection AddNotificationsModule(this IServiceCollection services, NotificationsOptions options)
    {
        services.AddSingleton(options);
        services.AddIntegrationEventHandler<InvitationIssued, SendInvitation>();
        services.AddIntegrationEventHandler<PasswordResetRequested, SendPasswordReset>();
        services.AddIntegrationEventHandler<MembershipGranted, SendMembershipGranted>();
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
            EmailRenderer.Text(forOperator ? "InvitationOperatorBody" : "InvitationCustomerBody", culture, storeName, invitation.CustomerName),
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
