using FCommerce.Catalog;
using FCommerce.Host.Media;
using FCommerce.Infrastructure.Search;
using FCommerce.Media;
using FCommerce.Host.Integration;
using FCommerce.Host.Localization;
using FCommerce.Infrastructure;
using FCommerce.Infrastructure.Persistence;
using FCommerce.Infrastructure.Postgres;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("Connection string 'Postgres' is not configured.");
var applyDatabaseChanges = builder.Configuration.GetValue<bool>("Database:ApplyChangesOnStartup");

// Infrastructure: database-independent parts + the PostgreSQL adapter.
builder.Services.AddFCommerceInfrastructure();
builder.Services.AddPostgres(connectionString, applySchemaChanges: applyDatabaseChanges);

builder.Services.AddFileSystemBlobStore(Path.Combine(builder.Environment.ContentRootPath, builder.Configuration["Media:StoragePath"] ?? "data/media"));
builder.Services.AddEmbeddedSearch(Path.Combine(builder.Environment.ContentRootPath, builder.Configuration["Search:StoragePath"] ?? "data/search"));

// Modules.
var localization = builder.Configuration.GetSection(LocalizationSettings.Section).Get<LocalizationSettings>() ?? new LocalizationSettings();
builder.Services.AddMediaModule();
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
builder.Services.AddRazorPages(options => options.Conventions.Add(new CultureRouteConvention()))
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
