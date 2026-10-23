using FCommerce.Access.Contracts;
using FCommerce.Application.Email;
using FCommerce.Application.Messaging;
using FCommerce.Infrastructure;
using FCommerce.Notifications.Templates;
using FCommerce.SharedKernel.Contact;
using Microsoft.Extensions.DependencyInjection;

namespace FCommerce.Notifications;

public sealed class NotificationsOptions
{
    /// <summary>Public address of the storefront, used to build links in e-mails, e.g. "https://b2b.example.com".</summary>
    public required Uri PublicUrl { get; init; }

    public string SellerName { get; set; } = "f.commerce";
}

public static class NotificationsModule
{
    /// <summary>Requires an <see cref="IEmailSender"/>.</summary>
    public static IServiceCollection AddNotificationsModule(this IServiceCollection services, NotificationsOptions options)
    {
        services.AddSingleton(options);
        services.AddSingleton<EmailRenderer>();
        services.AddIntegrationEventHandler<InvitationIssued, SendInvitation>();
        services.AddIntegrationEventHandler<PasswordResetRequested, SendPasswordReset>();
        return services;
    }
}

internal static class Links
{
    public static Uri To(NotificationsOptions options, string culture, string path, string token) =>
        new(options.PublicUrl, $"/{culture}/{path}?token={Uri.EscapeDataString(token)}");

    /// <summary>Addresses in events were validated when the account was created.</summary>
    public static EmailAddress Address(string email) =>
        EmailAddress.Parse(email).Match(address => address, error => throw new InvalidOperationException(error.Message));
}

internal sealed class SendInvitation(IEmailSender sender, EmailRenderer renderer, NotificationsOptions options) : IIntegrationEventHandler<InvitationIssued>
{
    public Task HandleAsync(InvitationIssued invitation, CancellationToken cancellationToken = default)
    {
        var culture = invitation.Culture;
        var forOperator = invitation.Audience == "operator";
        var email = new ActionEmail(
            EmailRenderer.Text(forOperator ? "InvitationOperatorSubject" : "InvitationCustomerSubject", culture, options.SellerName),
            EmailRenderer.Text("Greeting", culture, invitation.Name),
            EmailRenderer.Text(forOperator ? "InvitationOperatorBody" : "InvitationCustomerBody", culture, options.SellerName, invitation.CustomerName),
            EmailRenderer.Text("InvitationAction", culture),
            Links.To(options, culture, "invitation", invitation.Token),
            EmailRenderer.Text("InvitationExpiry", culture));
        return sender.SendAsync(renderer.Render(Links.Address(invitation.Email), invitation.Name, culture, email), cancellationToken);
    }
}

internal sealed class SendPasswordReset(IEmailSender sender, EmailRenderer renderer, NotificationsOptions options) : IIntegrationEventHandler<PasswordResetRequested>
{
    public Task HandleAsync(PasswordResetRequested reset, CancellationToken cancellationToken = default)
    {
        var culture = reset.Culture;
        var email = new ActionEmail(
            EmailRenderer.Text("ResetSubject", culture, options.SellerName),
            EmailRenderer.Text("Greeting", culture, reset.Name),
            EmailRenderer.Text("ResetBody", culture),
            EmailRenderer.Text("ResetAction", culture),
            Links.To(options, culture, "reset-password", reset.Token),
            EmailRenderer.Text("ResetExpiry", culture));
        return sender.SendAsync(renderer.Render(Links.Address(reset.Email), reset.Name, culture, email), cancellationToken);
    }
}
