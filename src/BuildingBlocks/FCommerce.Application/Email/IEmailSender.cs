using FCommerce.SharedKernel.Contact;

namespace FCommerce.Application.Email;

/// <summary>A rendered e-mail: HTML with a plain-text alternative.</summary>
public sealed record EmailMessage(EmailAddress To, string? ToName, string Subject, string Html, string Text);

/// <summary>Port for sending e-mail. Failures surface as exceptions (infrastructure), so the outbox retries.</summary>
public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}
