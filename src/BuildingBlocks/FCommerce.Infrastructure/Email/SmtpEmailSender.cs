using FCommerce.Application.Email;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using FCommerce.SharedKernel.Optional;

namespace FCommerce.Infrastructure.Email;

/// <summary>Bound from configuration; user name and password are absent for a local mail catcher.</summary>
[NullableEdge]
public sealed class SmtpOptions
{
    public const string Section = "Email";

    public string Host { get; set; } = "localhost";

    public int Port { get; set; } = 1025;

    /// <summary>None (local catcher), StartTls or SslOnConnect.</summary>
    public string Security { get; set; } = "None";

    public string? UserName { get; set; }

    public string? Password { get; set; }

    public string FromAddress { get; set; } = "no-reply@localhost";

    public string FromName { get; set; } = "f.commerce";
}

/// <summary><see cref="IEmailSender"/> over SMTP (MailKit); one connection per message, which suits low volumes.</summary>
internal sealed class SmtpEmailSender(SmtpOptions options) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        using var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(options.FromName, options.FromAddress));
        mime.To.Add(new MailboxAddress(message.ToName.ValueOr(string.Empty), message.To.Value));
        mime.Subject = message.Subject;
        mime.Body = new BodyBuilder { HtmlBody = message.Html, TextBody = message.Text }.ToMessageBody();

        using var client = new SmtpClient();
        await client.ConnectAsync(options.Host, options.Port, Enum.Parse<SecureSocketOptions>(options.Security, ignoreCase: true), cancellationToken);
        foreach (var userName in options.UserName.ToMaybe().Where(name => name.Length > 0).AsEnumerable())
        {
            await client.AuthenticateAsync(userName, options.Password.OrEmpty(), cancellationToken);
        }

        await client.SendAsync(mime, cancellationToken);
        await client.DisconnectAsync(quit: true, cancellationToken);
    }
}
