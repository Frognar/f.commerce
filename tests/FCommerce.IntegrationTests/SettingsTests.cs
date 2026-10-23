using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using SkiaSharp;

namespace FCommerce.IntegrationTests;

/// <summary>
/// A separate host: these tests change installation-wide settings (catalogue visibility), which must not disturb the
/// tests running in parallel on the shared host. Self-registration is not available in this installation.
/// </summary>
public sealed class IsolatedHostFixture : HostFixture
{
    protected override IWebHostBuilder Configure(IWebHostBuilder host) =>
        host.UseSetting("Features:Available:0", "customer-accounts")
            .UseSetting("Features:Available:1", "multi-customer-accounts")
            .UseSetting("Features:Available:2", "customer-admin-invitations")
            .UseSetting("Features:Available:3", "erp-contact-invitations")
            .UseSetting("Features:Available:4", "documents");
}

public sealed partial class SettingsTests(IsolatedHostFixture host) : IClassFixture<IsolatedHostFixture>
{
    private static readonly SemaphoreSlim Sequential = new(1, 1);

    private static async Task<long> VersionAsync(Browser admin) =>
        long.Parse(VersionField().Match((await admin.GetAsync("/pl/admin/settings")).Html).Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);

    private static List<KeyValuePair<string, string>> Form(long version, string visibility = "Public", string storeName = "f.commerce", string accent = "#1f5eff", params string[] features)
    {
        List<KeyValuePair<string, string>> fields =
        [
            new("Form.Version", version.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new("Form.CatalogVisibility", visibility),
            new("Form.StoreName", storeName),
            new("Form.AccentColor", accent),
            new("Form.DefaultContactTemplate", "buyer"),
        ];
        fields.AddRange((features.Length > 0 ? features : ["customer-accounts", "multi-customer-accounts", "customer-admin-invitations", "erp-contact-invitations", "documents"])
            .Select(f => new KeyValuePair<string, string>("Form.EnabledFeatures", f)));
        return fields;
    }

    /// <summary>Settings are global: tests of this class change them one at a time and restore the defaults.</summary>
    private async Task WithSettingsAsync(Func<Browser, Task> test)
    {
        await Sequential.WaitAsync(TestContext.Current.CancellationToken);
        using var admin = await host.AdminAsync();
        try
        {
            await test(admin);
        }
        finally
        {
            await admin.SubmitAsync("/pl/admin/settings", Form(await VersionAsync(admin)));
            Sequential.Release();
        }
    }

    [Fact]
    public Task Catalog_visibility_changes_without_a_restart() =>
        WithSettingsAsync(async admin =>
        {
            using var visitor = host.NewBrowser();
            Assert.Equal(HttpStatusCode.OK, (await visitor.GetAsync("/pl/catalog")).Status);

            var (_, html, _) = await admin.SubmitAsync("/pl/admin/settings", Form(await VersionAsync(admin), visibility: "SignedInOnly"));

            Assert.Contains("Zapisano zmiany.", html, StringComparison.Ordinal);
            Assert.StartsWith("/pl/sign-in?returnUrl=%2Fpl%2Fcatalog", (await visitor.GetAsync("/pl/catalog")).Location, StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/pl/catalog")).Status);
        });

    [Fact]
    public Task Saving_over_someone_elses_newer_changes_is_refused() =>
        WithSettingsAsync(async admin =>
        {
            var version = await VersionAsync(admin);

            await admin.SubmitAsync("/pl/admin/settings", Form(version, storeName: "Pierwsza zmiana"));
            var (_, html, _) = await admin.SubmitAsync("/pl/admin/settings", Form(version, storeName: "Druga zmiana"));

            Assert.Contains("Ktoś zmienił ustawienia w międzyczasie.", html, StringComparison.Ordinal);
            Assert.Contains("Pierwsza zmiana", (await admin.GetAsync("/pl")).Html, StringComparison.Ordinal);
        });

    [Fact]
    public Task Only_available_features_are_offered_and_requirements_are_checked() =>
        WithSettingsAsync(async admin =>
        {
            var page = (await admin.GetAsync("/pl/admin/settings")).Html;
            var unavailable = await admin.SubmitAsync("/pl/admin/settings", Form(await VersionAsync(admin), features: ["customer-accounts", "self-registration"]));
            var missingRequirement = await admin.SubmitAsync("/pl/admin/settings", Form(await VersionAsync(admin), features: ["documents"]));

            Assert.Contains("value=\"documents\"", page, StringComparison.Ordinal);
            Assert.DoesNotContain("value=\"self-registration\"", page, StringComparison.Ordinal);
            Assert.Contains("Ta funkcja nie jest dostępna w tej instalacji.", unavailable.Html, StringComparison.Ordinal);
            Assert.Contains("Włączona funkcja wymaga innej, wyłączonej funkcji.", missingRequirement.Html, StringComparison.Ordinal);
        });

    [Fact]
    public Task Branding_shows_in_the_storefront_and_in_e_mails() =>
        WithSettingsAsync(async admin =>
        {
            using var logo = new SKBitmap(200, 80);
            using (var canvas = new SKCanvas(logo))
            {
                canvas.Clear(SKColors.DarkOrange);
            }

            using var png = SKImage.FromBitmap(logo).Encode(SKEncodedImageFormat.Png, 100);
            var saved = await admin.SubmitWithFileAsync("/pl/admin/settings", Form(await VersionAsync(admin), storeName: "Hurtownia Śruba", accent: "#aa3300"), "Logo", png.ToArray(), "image/png");
            var home = (await host.NewBrowser().GetAsync("/pl")).Html;
            var email = $"brand-{Guid.NewGuid():N}"[..20] + "@test.example.pl";
            await admin.SubmitAsync(
                "/pl/admin/operators",
                new Dictionary<string, string> { ["Form.Email"] = email, ["Form.Name"] = "Nowy", ["Form.Culture"] = "pl", ["Form.CustomerScope"] = "all" },
                "/pl/admin/operators?handler=Invite");
            var invitation = await host.Mail.WaitForAsync(email);

            Assert.Contains("Zapisano zmiany.", saved.Html, StringComparison.Ordinal);
            Assert.Contains("--color-accent: #aa3300", home, StringComparison.Ordinal);
            Assert.Matches("class=\"brand-logo\" src=\"/media/images/[0-9a-f]{64}/thumb\"", home);
            Assert.Contains("Hurtownia Śruba", home, StringComparison.Ordinal);
            Assert.Equal("Dostęp do panelu Hurtownia Śruba", invitation.Subject);
        });

    [Fact]
    public Task History_records_who_changed_what() =>
        WithSettingsAsync(async admin =>
        {
            await admin.SubmitAsync("/pl/admin/settings", Form(await VersionAsync(admin), visibility: "SignedInOnly", features: ["customer-accounts", "documents"]));

            var page = (await admin.GetAsync("/pl/admin/settings")).Html;

            Assert.Matches("<td>Administrator</td>\\s*<td>[^<]*widoczność katalogu[^<]*wyłączono: Jedno konto dla wielu firm", page);
        });

    [Fact]
    public async Task Settings_are_only_for_operators_who_manage_them()
    {
        using var visitor = host.NewBrowser();

        Assert.StartsWith("/pl/sign-in", (await visitor.GetAsync("/pl/admin/settings")).Location, StringComparison.Ordinal);
    }

    [GeneratedRegex("name=\"Form.Version\" value=\"(\\d+)\"")]
    private static partial Regex VersionField();
}
