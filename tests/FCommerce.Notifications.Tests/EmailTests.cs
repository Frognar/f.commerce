using FCommerce.Access.Contracts;
using FCommerce.Application.Email;
using FCommerce.Notifications.Templates;

namespace FCommerce.Notifications.Tests;

public sealed class EmailTests
{
    private sealed class Recorder : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = [];

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }
    }

    private static readonly NotificationsOptions Options = new() { PublicUrl = new Uri("https://b2b.example.com"), SellerName = "Hurtownia <Śruba>" };

    private static async Task<EmailMessage> SendInvitationAsync(string culture, string name, string audience)
    {
        var recorder = new Recorder();
        await new SendInvitation(recorder, new EmailRenderer(Options), Options).HandleAsync(
            new InvitationIssued(Guid.NewGuid(), "jan@firma.pl", name, culture, "tok+en/1", audience, "Firma S.A."),
            TestContext.Current.CancellationToken);
        return Assert.Single(recorder.Sent);
    }

    [Fact]
    public async Task Invitation_is_written_in_the_recipients_language_with_a_link_to_the_invitation_page()
    {
        var polish = await SendInvitationAsync("pl", "Jan", "customer");
        var english = await SendInvitationAsync("en", "Jan", "operator");

        Assert.Equal("Twoje konto w Hurtownia <Śruba>", polish.Subject);
        Assert.Contains("w imieniu firmy Firma S.A.", polish.Text, StringComparison.Ordinal);
        Assert.Contains("https://b2b.example.com/pl/invitation?token=tok%2Ben%2F1", polish.Text, StringComparison.Ordinal);
        Assert.Equal("Your access to the Hurtownia <Śruba> back-office", english.Subject);
        Assert.Contains("https://b2b.example.com/en/invitation?token=", english.Html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task User_provided_text_is_html_encoded()
    {
        var email = await SendInvitationAsync("pl", "<script>alert(1)</script>", "customer");

        Assert.DoesNotContain("<script>", email.Html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", email.Html, StringComparison.Ordinal);
        Assert.Contains("Hurtownia &lt;Śruba&gt;", email.Html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Password_reset_links_to_the_reset_page()
    {
        var recorder = new Recorder();

        await new SendPasswordReset(recorder, new EmailRenderer(Options), Options).HandleAsync(
            new PasswordResetRequested(Guid.NewGuid(), "jan@firma.pl", "Jan", "en", "abc"), TestContext.Current.CancellationToken);

        var email = Assert.Single(recorder.Sent);
        Assert.Contains("https://b2b.example.com/en/reset-password?token=abc", email.Text, StringComparison.Ordinal);
        Assert.Contains("valid for 1 hour", email.Text, StringComparison.Ordinal);
    }
}
