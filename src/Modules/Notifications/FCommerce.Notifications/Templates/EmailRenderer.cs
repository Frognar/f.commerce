using System.Globalization;
using System.Net;
using System.Resources;
using FCommerce.Application.Email;
using FCommerce.SharedKernel.Contact;

namespace FCommerce.Notifications.Templates;

/// <summary>One call-to-action e-mail: greeting, paragraph, button, note — rendered as HTML and plain text.</summary>
internal sealed record ActionEmail(string Subject, string Greeting, string Body, string ActionText, Uri ActionUrl, string Note);

/// <summary>
/// Texts come from resources per language (EmailTexts.resx, EmailTexts.pl.resx …); an installation can later override
/// them. Everything user-provided is HTML-encoded.
/// </summary>
internal static class EmailRenderer
{
    private static readonly ResourceManager Texts = new("FCommerce.Notifications.Templates.EmailTexts", typeof(EmailRenderer).Assembly);

    public static string Text(string key, string culture, params object[] arguments)
    {
        var info = CultureInfo.GetCultureInfo(culture);
        var format = Texts.GetString(key, info) ?? key;
        return string.Format(info, format, arguments);
    }

    public static EmailMessage Render(EmailAddress to, string toName, string culture, string storeName, ActionEmail email)
    {
        var footer = Text("Footer", culture, storeName);
        var html = $"""
            <!DOCTYPE html>
            <html lang="{Encode(culture)}">
            <body style="margin:0;padding:24px;background:#f6f7f9;font-family:system-ui,-apple-system,'Segoe UI',Roboto,sans-serif;color:#1b1f24">
              <div style="max-width:560px;margin:0 auto;background:#fff;border:1px solid #dfe3e8;border-radius:10px;padding:32px">
                <p style="font-weight:700;margin:0 0 24px">{Encode(storeName)}</p>
                <p>{Encode(email.Greeting)}</p>
                <p>{Encode(email.Body)}</p>
                <p style="margin:28px 0">
                  <a href="{Encode(email.ActionUrl.AbsoluteUri)}" style="background:#1f5eff;color:#fff;padding:12px 20px;border-radius:8px;text-decoration:none;display:inline-block">{Encode(email.ActionText)}</a>
                </p>
                <p style="color:#5b6573;font-size:14px">{Encode(email.Note)}</p>
              </div>
              <p style="max-width:560px;margin:16px auto 0;color:#5b6573;font-size:12px">{Encode(footer)}</p>
            </body>
            </html>
            """;
        var text = $"{email.Greeting}\n\n{email.Body}\n\n{email.ActionText}: {email.ActionUrl.AbsoluteUri}\n\n{email.Note}\n\n--\n{footer}\n";
        return new EmailMessage(to, toName, email.Subject, html, text);
    }

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}
