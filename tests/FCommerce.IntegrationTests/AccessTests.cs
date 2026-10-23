using System.Net;
using System.Text.RegularExpressions;

namespace FCommerce.IntegrationTests;

public sealed partial class AccessTests(HostFixture host)
{
    private const string AdminPassword = "platform owner passphrase 1";
    private static readonly SemaphoreSlim AdminGate = new(1, 1);
    private static bool adminActivated;

    private Browser NewBrowser() => new(host.CreateClient());

    private static string UniqueEmail(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..20] + "@test.example.pl";

    /// <summary>Opens the invitation link from the latest invitation e-mail and chooses a password.</summary>
    private async Task<(HttpStatusCode Status, string? Location)> AcceptInvitationAsync(Browser browser, string email, string password)
    {
        var mail = await host.Mail.WaitForAsync(email, m => m.Text.Contains("/invitation?token=", StringComparison.Ordinal));
        var link = new Uri(InvitationLink().Match(mail.Text).Value);
        var (status, _, location) = await browser.SubmitAsync(link.PathAndQuery, new Dictionary<string, string> { ["Password"] = password, ["Confirmation"] = password });
        return (status, location);
    }

    private static Task<(HttpStatusCode Status, string Html, string? Location)> SignInAsync(Browser browser, string email, string password) =>
        browser.SubmitAsync("/pl/sign-in", new Dictionary<string, string> { ["Email"] = email, ["Password"] = password });

    /// <summary>The bootstrap administrator, activated once per test run.</summary>
    private async Task<Browser> AdminAsync()
    {
        var browser = NewBrowser();
        await AdminGate.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            if (!adminActivated)
            {
                await AcceptInvitationAsync(browser, HostFixture.AdminEmail, AdminPassword);
                adminActivated = true;
                return browser;
            }
        }
        finally
        {
            AdminGate.Release();
        }

        await SignInAsync(browser, HostFixture.AdminEmail, AdminPassword);
        return browser;
    }

    private static async Task<string> InviteOperatorAsync(Browser admin, string email, bool manageOperators = false)
    {
        var fields = new Dictionary<string, string>
        {
            ["Form.Email"] = email,
            ["Form.Name"] = "Anna Handlowiec",
            ["Form.Culture"] = "pl",
            ["Form.CanManageOperators"] = manageOperators ? "true" : "false",
            ["Form.CanDecideRegistrations"] = "true",
            ["Form.CustomerScope"] = "as-salesperson",
        };
        var (status, html, _) = await admin.SubmitAsync("/pl/admin/operators", fields, "/pl/admin/operators?handler=Invite");
        Assert.Equal(HttpStatusCode.OK, status);
        return html;
    }

    [Fact]
    public async Task Bootstrap_invites_the_first_administrator_who_lands_in_the_back_office()
    {
        using var admin = await AdminAsync();

        var (status, html, _) = await admin.GetAsync("/pl/admin");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("Panel sprzedawcy", html, StringComparison.Ordinal);
        Assert.Contains("href=\"/pl/admin/operators\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Anonymous_visitors_are_sent_to_sign_in_and_back()
    {
        using var browser = NewBrowser();

        var (status, _, location) = await browser.GetAsync("/pl/admin/operators");

        Assert.Equal(HttpStatusCode.Redirect, status);
        Assert.Equal("/pl/sign-in?returnUrl=%2Fpl%2Fadmin%2Foperators", location);
    }

    [Fact]
    public async Task Invited_operators_get_only_their_rights_and_lose_the_session_when_revoked()
    {
        using var admin = await AdminAsync();
        var email = UniqueEmail("sales");
        using var salesperson = NewBrowser();

        var page = await InviteOperatorAsync(admin, email);
        var accepted = await AcceptInvitationAsync(salesperson, email, "salesperson password 1");
        var dashboard = await salesperson.GetAsync("/pl/admin");
        var operators = await salesperson.GetAsync("/pl/admin/operators");

        Assert.Contains("Zaproszenie wysłane.", page, StringComparison.Ordinal);
        Assert.Equal("/pl/admin", accepted.Location);
        Assert.Equal(HttpStatusCode.OK, dashboard.Status);
        Assert.Equal("/pl/access-denied", operators.Location);

        var id = OperatorRow().Matches((await admin.GetAsync("/pl/admin/operators")).Html)
            .First(m => m.Groups["email"].Value == email).Groups["id"].Value;
        await admin.SubmitAsync("/pl/admin/operators", new Dictionary<string, string>(), $"/pl/admin/operators?handler=Revoke&id={id}");

        var afterRevoke = await salesperson.GetAsync("/pl/admin");
        Assert.StartsWith("/pl/sign-in", afterRevoke.Location, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_last_operator_manager_cannot_lose_the_right()
    {
        using var admin = await AdminAsync();
        var html = (await admin.GetAsync("/pl/admin/operators")).Html;
        var adminId = OperatorRow().Matches(html).First(m => m.Groups["email"].Value == HostFixture.AdminEmail).Groups["id"].Value;

        var (_, result, _) = await admin.SubmitAsync(
            "/pl/admin/operators",
            new Dictionary<string, string> { ["Form.CanManageSettings"] = "true", ["Form.CustomerScope"] = "all" },
            $"/pl/admin/operators?handler=Change&id={adminId}");

        Assert.Contains("Co najmniej jeden operator musi móc zarządzać operatorami.", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Five_wrong_passwords_lock_the_account_for_a_while()
    {
        using var admin = await AdminAsync();
        var email = UniqueEmail("lock");
        using var person = NewBrowser();
        await InviteOperatorAsync(admin, email);
        await AcceptInvitationAsync(person, email, "the right password 1");
        using var attacker = NewBrowser();

        for (var i = 0; i < 4; i++)
        {
            Assert.Contains("Nieprawidłowy e-mail lub hasło.", (await SignInAsync(attacker, email, "wrong password guess")).Html, StringComparison.Ordinal);
        }

        var fifth = await SignInAsync(attacker, email, "wrong password guess");
        var rightButLocked = await SignInAsync(attacker, email, "the right password 1");

        Assert.Contains("Zbyt wiele nieudanych prób", fifth.Html, StringComparison.Ordinal);
        Assert.Contains("Zbyt wiele nieudanych prób", rightButLocked.Html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Password_reset_by_e_mail_replaces_the_password()
    {
        using var admin = await AdminAsync();
        var email = UniqueEmail("reset");
        using var person = NewBrowser();
        await InviteOperatorAsync(admin, email);
        await AcceptInvitationAsync(person, email, "the first password 1");

        var requested = await person.SubmitAsync("/pl/forgot-password", new Dictionary<string, string> { ["Email"] = email });
        var unknown = await person.SubmitAsync("/pl/forgot-password", new Dictionary<string, string> { ["Email"] = UniqueEmail("nobody") });
        var mail = await host.Mail.WaitForAsync(email, m => m.Text.Contains("/reset-password?token=", StringComparison.Ordinal));
        var link = new Uri(ResetLink().Match(mail.Text).Value);
        var reset = await person.SubmitAsync(link.PathAndQuery, new Dictionary<string, string> { ["Password"] = "the second password 2", ["Confirmation"] = "the second password 2" });
        var reused = await person.SubmitAsync(link.PathAndQuery, new Dictionary<string, string> { ["Password"] = "the third password 3", ["Confirmation"] = "the third password 3" });

        Assert.Equal(requested.Html.Contains("wysłaliśmy", StringComparison.Ordinal), unknown.Html.Contains("wysłaliśmy", StringComparison.Ordinal));
        Assert.Contains("Hasło zostało zmienione.", reset.Html, StringComparison.Ordinal);
        Assert.Contains("Link jest nieprawidłowy lub wygasł.", reused.Html, StringComparison.Ordinal);
        using var fresh = NewBrowser();
        Assert.Contains("Nieprawidłowy e-mail lub hasło.", (await SignInAsync(fresh, email, "the first password 1")).Html, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Redirect, (await SignInAsync(fresh, email, "the second password 2")).Status);
    }

    [Fact]
    public async Task Changing_the_password_signs_out_other_sessions_but_keeps_this_one()
    {
        using var admin = await AdminAsync();
        var email = UniqueEmail("change");
        using var laptop = NewBrowser();
        using var phone = NewBrowser();
        await InviteOperatorAsync(admin, email);
        await AcceptInvitationAsync(laptop, email, "the first password 1");
        await SignInAsync(phone, email, "the first password 1");
        Assert.Equal(HttpStatusCode.OK, (await phone.GetAsync("/pl/account")).Status);

        var changed = await laptop.SubmitAsync(
            "/pl/account",
            new Dictionary<string, string> { ["CurrentPassword"] = "the first password 1", ["NewPassword"] = "the second password 2", ["Confirmation"] = "the second password 2" },
            "/pl/account?handler=Password");

        Assert.Contains("Hasło zostało zmienione.", changed.Html, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await laptop.GetAsync("/pl/account")).Status);
        Assert.StartsWith("/pl/sign-in", (await phone.GetAsync("/pl/account")).Location, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invitation_links_work_once()
    {
        using var admin = await AdminAsync();
        var email = UniqueEmail("once");
        await InviteOperatorAsync(admin, email);
        var mail = await host.Mail.WaitForAsync(email);
        var link = new Uri(InvitationLink().Match(mail.Text).Value);
        using var first = NewBrowser();
        using var second = NewBrowser();

        await first.SubmitAsync(link.PathAndQuery, new Dictionary<string, string> { ["Password"] = "a good password 1", ["Confirmation"] = "a good password 1" });
        var again = await second.GetAsync(link.PathAndQuery);

        Assert.Contains("Link jest nieprawidłowy lub wygasł.", again.Html, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"https?://\S+/invitation\?token=[A-Za-z0-9_%-]+")]
    private static partial Regex InvitationLink();

    [GeneratedRegex(@"https?://\S+/reset-password\?token=[A-Za-z0-9_%-]+")]
    private static partial Regex ResetLink();

    [GeneratedRegex("<td>(?<email>[^<@]+@[^<]+)</td>[\\s\\S]*?<form id=\"operator-(?<id>[0-9a-f]{32})\"")]
    private static partial Regex OperatorRow();
}
