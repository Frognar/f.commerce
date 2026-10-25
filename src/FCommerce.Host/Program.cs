using FCommerce.Access;
using FCommerce.Cart;
using FCommerce.Catalog;
using FCommerce.Customers;
using FCommerce.Documents;
using FCommerce.Host.Security;
using FCommerce.Inventory;
using FCommerce.Notifications;
using FCommerce.Ordering;
using FCommerce.Pricing;
using FCommerce.Settings;
using FCommerce.SharedKernel.Features;
using System.Collections.Immutable;
using Microsoft.AspNetCore.Authentication.Cookies;
using FCommerce.Host.Media;
using FCommerce.Infrastructure.Search;
using FCommerce.Media;
using FCommerce.Host.Integration;
using FCommerce.Host.Localization;
using FCommerce.Infrastructure;
using FCommerce.Infrastructure.Persistence;
using FCommerce.Infrastructure.Postgres;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("Connection string 'Postgres' is not configured.");
var applyDatabaseChanges = builder.Configuration.GetValue<bool>("Database:ApplyChangesOnStartup");

// Infrastructure: database-independent parts + the PostgreSQL adapter.
builder.Services.AddFCommerceInfrastructure();
builder.Services.AddPostgres(connectionString, applySchemaChanges: applyDatabaseChanges);

// Cookies and antiforgery tokens must survive restarts: keys are persisted.
builder.Services.AddDataProtection()
    .SetApplicationName("fcommerce")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(builder.Environment.ContentRootPath, builder.Configuration["DataProtection:KeysPath"] ?? "data/keys")));
builder.Services.AddSmtpEmail(builder.Configuration.GetSection(FCommerce.Infrastructure.Email.SmtpOptions.Section).Get<FCommerce.Infrastructure.Email.SmtpOptions>() ?? new());
builder.Services.AddFileSystemBlobStore(Path.Combine(builder.Environment.ContentRootPath, builder.Configuration["Media:StoragePath"] ?? "data/media"));
builder.Services.AddEmbeddedSearch(Path.Combine(builder.Environment.ContentRootPath, builder.Configuration["Search:StoragePath"] ?? "data/search"));

// Identity and access: cookie session, ABAC policies, platform switches.
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddScoped<AccessControl>();
builder.Services.AddScoped<FCommerce.Host.Pages.Shared.CustomerUsersActions>();
builder.Services.AddScoped<FCommerce.Host.Pages.Company.CompanyContext>();
builder.Services.AddScoped<FCommerce.Host.Pages.Catalog.CatalogOffers>();
builder.Services.AddScoped<FCommerce.Host.Pages.Cart.CartAccess>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options => SessionCookie.Configure(options, requireHttps: builder.Environment.IsProduction()));
builder.Services.AddAbacAuthorization();
builder.Services.AddAuthenticationRateLimits(builder.Configuration.GetValue("Security:AuthenticationAttemptsPerMinute", 10));

// Modules. What this installation offers is set at deployment; the back-office switches within it.
var availableFeatures = (builder.Configuration.GetSection("Features:Available").Get<string[]>()
        ?? [.. Feature.All.Select(f => f.Id.Value)])
    .Select(id => FeatureId.Parse(id).Match(f => f, e => throw new InvalidOperationException($"Features:Available: {e.Message}")))
    .ToImmutableHashSet();
builder.Services.AddSettingsModule(new SettingsOptions(availableFeatures));
var localization = builder.Configuration.GetSection(LocalizationSettings.Section).Get<LocalizationSettings>() ?? new LocalizationSettings();
var accessOptions = builder.Configuration.GetSection("Access").Get<AccessOptions>() ?? new AccessOptions();
accessOptions.DefaultCulture = builder.Configuration["Access:DefaultCulture"] ?? localization.DefaultCulture;
builder.Services.AddAccessModule(accessOptions);
builder.Services.AddNotificationsModule(new NotificationsOptions
{
    PublicUrl = new Uri(builder.Configuration["Platform:PublicUrl"] ?? throw new InvalidOperationException("Platform:PublicUrl is not configured.")),
    SellerCulture = localization.DefaultCulture,
});
builder.Services.AddMediaModule();
builder.Services.AddCustomersModule();
var sellerTimeZone = builder.Configuration["Platform:TimeZone"] ?? "Europe/Warsaw";
builder.Services.AddDocumentsModule(new DocumentsOptions { TimeZone = sellerTimeZone });
builder.Services.AddPricingModule();
builder.Services.AddInventoryModule(new InventoryOptions { TimeZone = sellerTimeZone });
builder.Services.AddCartModule(new CartOptions { DefaultCulture = localization.DefaultCulture });
builder.Services.AddOrderingModule(new OrderingOptions
{
    NumberPrefix = builder.Configuration["Ordering:NumberPrefix"] ?? "WWW",
    TimeZone = sellerTimeZone,
});
builder.Services.AddCatalogModule(options =>
{
    options.DefaultCulture = localization.DefaultCulture;
    foreach (var culture in localization.SupportedCultures)
    {
        options.Cultures.Add(culture);
    }
});

// Localization: culture comes from the URL ("/pl/…"); "/" negotiates it from Accept-Language.
// Invalid settings stop the host on startup (ValidateOnStart), so the factory below only sees valid ones.
builder.Services.AddOptions<LocalizationSettings>().Bind(builder.Configuration.GetSection(LocalizationSettings.Section)).ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<LocalizationSettings>, LocalizationSettingsValidator>();
builder.Services.AddSingleton(sp => SupportedCultures.From(sp.GetRequiredService<IOptions<LocalizationSettings>>().Value)
    .Match(cultures => cultures, error => throw new OptionsValidationException(LocalizationSettings.Section, typeof(LocalizationSettings), [error.Message])));
builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");
// Emit non-Latin text (ą, ł, ö, …) as-is instead of HTML entities; encoding still escapes markup characters.
builder.Services.Configure<Microsoft.Extensions.WebEncoders.WebEncoderOptions>(options =>
    options.TextEncoderSettings = new System.Text.Encodings.Web.TextEncoderSettings(System.Text.Unicode.UnicodeRanges.All));
builder.Services.Configure<RouteOptions>(options => options.ConstraintMap[CultureRouteConstraint.Name] = typeof(CultureRouteConstraint));
builder.Services.AddOptions<RequestLocalizationOptions>().Configure<SupportedCultures>((options, cultures) =>
{
    options.SetDefaultCulture(cultures.Default.Value);
    options.SupportedCultures = cultures.CultureInfos;
    options.SupportedUICultures = cultures.CultureInfos;
    options.RequestCultureProviders.Insert(0, new Microsoft.AspNetCore.Localization.Routing.RouteDataRequestCultureProvider
    {
        RouteDataStringKey = CultureRouteConvention.RouteKey,
        UIRouteDataStringKey = CultureRouteConvention.RouteKey,
    });
});

// Web UI (Razor Pages + htmx) and public API (Minimal APIs) are thin adapters over module contracts.
builder.Services.AddRazorPages(options =>
    {
        options.Conventions.Add(new CultureRouteConvention());
        options.Conventions.AuthorizePage("/Catalog/Index", Policies.CatalogRead);
        options.Conventions.AuthorizePage("/Products/Details", Policies.CatalogRead);
        options.Conventions.AuthorizeFolder("/Account", Policies.OwnAccount);
        options.Conventions.AuthorizeFolder("/Company", Policies.OwnAccount);
        options.Conventions.AuthorizeFolder("/Cart", Policies.OwnAccount);
        options.Conventions.AuthorizeFolder("/Admin", Policies.BackOffice);
        options.Conventions.AuthorizePage("/Admin/Operators", Policies.OperatorsManage);
        options.Conventions.AuthorizePage("/Admin/Settings", Policies.SettingsManage);
        options.Conventions.AuthorizeFolder("/Admin/Registrations", Policies.RegistrationsDecide);
    })
    .AddViewLocalization();
builder.Services.AddAntiforgery(options => options.HeaderName = "RequestVerificationToken");
builder.Services.AddOpenApi("v1");
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();

var app = builder.Build();

if (applyDatabaseChanges)
{
    await app.Services.MigrateDatabasesAsync();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();
app.UseRequestLocalization();
app.UseFeatureGate();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorPages().WithStaticAssets();
app.MapGet("/", CultureRedirect.ToPreferredCulture).ExcludeFromDescription();
app.MapHealthChecks("/health");
app.MapOpenApi("/openapi/{documentName}.json");
app.MapIntegrationApi();
app.MapMediaEndpoints();

await app.RunAsync();

/// <summary>Entry point; public for WebApplicationFactory in integration tests.</summary>
public partial class Program;
