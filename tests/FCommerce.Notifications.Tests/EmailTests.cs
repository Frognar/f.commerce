using FCommerce.Access.Contracts;
using FCommerce.Application.Email;
using FCommerce.Application.Messaging;
using FCommerce.Settings.Contracts;

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

    private static readonly NotificationsOptions Options = new() { PublicUrl = new Uri("https://b2b.example.com") };

    /// <summary>Settings with a store name that needs HTML encoding.</summary>
    private sealed class Settings(string? operatorMailbox = null) : IQueryHandler<GetPlatformSettings, PlatformSettingsView>
    {
        public Task<PlatformSettingsView> HandleAsync(GetPlatformSettings query, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PlatformSettingsView(1, "Public", [], new BrandingView("Hurtownia <Śruba>", "#1f5eff", null), new ContactView(null, null, operatorMailbox), null, "buyer"));
    }

    private static async Task<EmailMessage> SendInvitationAsync(string culture, string name, string audience)
    {
        var recorder = new Recorder();
        await new SendInvitation(recorder, Options, new Settings()).HandleAsync(
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

        await new SendPasswordReset(recorder, Options, new Settings()).HandleAsync(
            new PasswordResetRequested(Guid.NewGuid(), "jan@firma.pl", "Jan", "en", "abc"), TestContext.Current.CancellationToken);

        var email = Assert.Single(recorder.Sent);
        Assert.Contains("https://b2b.example.com/en/reset-password?token=abc", email.Text, StringComparison.Ordinal);
        Assert.Contains("valid for 1 hour", email.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Access_to_another_company_names_it_and_links_to_sign_in()
    {
        var recorder = new Recorder();

        await new SendMembershipGranted(recorder, Options, new Settings()).HandleAsync(
            new MembershipGranted(Guid.NewGuid(), "jan@firma.pl", "Jan", "pl", "Druga Firma sp. z o.o."), TestContext.Current.CancellationToken);

        var email = Assert.Single(recorder.Sent);
        Assert.Equal("Dostęp do firmy Druga Firma sp. z o.o. w Hurtownia <Śruba>", email.Subject);
        Assert.Contains("https://b2b.example.com/pl/sign-in", email.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Operators_hear_about_registration_requests_only_when_a_mailbox_is_set()
    {
        var recorder = new Recorder();
        var id = Guid.NewGuid();
        var submitted = new RegistrationSubmitted(id, "Nowa Firma", "PL5261040828", "Jan Kowalski", "jan@firma.pl");

        await new NotifyOperatorsOfRegistration(recorder, Options, new Settings()).HandleAsync(submitted, TestContext.Current.CancellationToken);
        await new NotifyOperatorsOfRegistration(recorder, Options, new Settings("biuro@sprzedawca.pl")).HandleAsync(submitted, TestContext.Current.CancellationToken);

        var email = Assert.Single(recorder.Sent);
        Assert.Equal("biuro@sprzedawca.pl", email.To.Value);
        Assert.Contains($"https://b2b.example.com/pl/admin/registrations/{id}", email.Text, StringComparison.Ordinal);
        Assert.Contains("Nowa Firma (NIP PL5261040828)", email.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rejection_gives_the_reason_in_the_applicants_language()
    {
        var recorder = new Recorder();

        await new SendRegistrationRejected(recorder, Options, new Settings()).HandleAsync(
            new RegistrationRejected(Guid.NewGuid(), "jan@firma.pl", "Jan", "en", "Nowa Firma", "We sell to registered businesses only."),
            TestContext.Current.CancellationToken);

        var email = Assert.Single(recorder.Sent);
        Assert.Contains("Reason: We sell to registered businesses only.", email.Text, StringComparison.Ordinal);
    }
}
