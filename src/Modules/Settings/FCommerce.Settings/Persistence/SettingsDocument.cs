using System.Collections.Immutable;
using System.Text.Json;
using FCommerce.Authorization.Core;
using FCommerce.Infrastructure.Persistence;
using FCommerce.Infrastructure.Serialization;
using FCommerce.Settings.Domain;
using FCommerce.SharedKernel.Contact;
using FCommerce.SharedKernel.Features;
using FCommerce.SharedKernel.Optional;

namespace FCommerce.Settings.Persistence;

/// <summary>
/// Storage shape of <see cref="PlatformSettings"/>; reading it back trusts the values validated when saved, but not a
/// document that cannot be one (a malformed colour, half of a group). Fields added later are at the end and missing
/// in older documents: a missing group reads as its default.
/// </summary>
/// <param name="KnownFeatures">The feature catalogue when saved: features added since then get their default state.</param>
internal sealed record SettingsDocument(
    string CatalogVisibility,
    string[] EnabledFeatures,
    string StoreName,
    string AccentColor,
    string? LogoImageId,
    string? SupportEmail,
    string? SupportPhone,
    string? OperatorNotifications,
    string? DefaultPriceListCode,
    string DefaultContactTemplate,
    string? StockDisplay,
    decimal? LowStockThreshold,
    string[]? KnownFeatures)
{
    /// <summary>The catalogue of documents saved before <see cref="KnownFeatures"/> was stored.</summary>
    private static readonly string[] FirstFeatures =
        ["customer-accounts", "multi-customer-accounts", "customer-admin-invitations", "erp-contact-invitations", "self-registration", "documents"];

    public static string Write(PlatformSettings settings) =>
        JsonSerializer.Serialize(
            new SettingsDocument(
                settings.CatalogVisibility.ToString(),
                [.. settings.EnabledFeatures.Ids.Select(f => f.Value).Order(StringComparer.Ordinal)],
                settings.Branding.StoreName.Value,
                settings.Branding.AccentColor.Value,
                settings.Branding.Logo.Map(l => l.Id).OrNull(),
                settings.Contact.SupportEmail.Map(e => e.Value).OrNull(),
                settings.Contact.SupportPhone.Map(p => p.Value).OrNull(),
                settings.Contact.OperatorNotifications.Map(e => e.Value).OrNull(),
                settings.Commerce.DefaultPriceList.Map(p => p.Value).OrNull(),
                settings.Commerce.DefaultContactTemplate.Value,
                settings.Stock.Mode.ToString(),
                settings.Stock.LowThreshold.Value,
                [.. Feature.All.Select(f => f.Id.Value).Order(StringComparer.Ordinal)]),
            PersistenceJson.Options);

    /// <summary>
    /// Features no longer known to the code or no longer available in this installation are dropped (they are off);
    /// features added to the code since saving are on when on by default (see <see cref="Domain.EnabledFeatures.Restore"/>).
    /// </summary>
    public static PlatformSettings Read(string json, long version, ImmutableHashSet<FeatureId> available)
    {
        var document = Stored.Required(JsonSerializer.Deserialize<SettingsDocument>(json, PersistenceJson.Options), "settings document");
        return Persisted.Rehydrate<PlatformSettings, PlatformSettings.Stored>(new(
            version,
            Stored.Enum<CatalogVisibility>(Stored.Required(document.CatalogVisibility, "catalog visibility"), "catalog visibility"),
            Domain.EnabledFeatures.Restore(
                Features(Stored.Required(document.EnabledFeatures, "enabled features")),
                [.. Features(document.KnownFeatures ?? FirstFeatures)],
                available),
            new Branding(
                Persisted.Rehydrate<StoreName, string>(Stored.Required(document.StoreName, "store name")),
                Persisted.Rehydrate<HexColor, string>(Stored.Required(document.AccentColor, "accent colour")),
                document.LogoImageId.ToMaybe().Map(Persisted.Rehydrate<LogoImage, string>)),
            new SellerContact(
                Email(document.SupportEmail),
                document.SupportPhone.ToMaybe().Map(Persisted.Rehydrate<Phone, string>),
                Email(document.OperatorNotifications)),
            new CommerceDefaults(
                document.DefaultPriceListCode.ToMaybe().Map(Persisted.Rehydrate<PriceListReference, string>),
                Persisted.Rehydrate<ContactTemplate, string>(Stored.Required(document.DefaultContactTemplate, "default contact template"))),
            (document.StockDisplay, document.LowStockThreshold) switch
            {
                (null, null) => Domain.StockDisplay.Default,
                ({ } mode, { } threshold) => new Domain.StockDisplay(
                    Stored.Enum<StockDisplayMode>(mode, "stock display"),
                    Persisted.Rehydrate<LowStockThreshold, decimal>(threshold)),
                _ => throw Stored.Corrupt("stock display without its low threshold, or the other way round."),
            }));
    }

    private static IEnumerable<FeatureId> Features(IEnumerable<string> ids) => ids.Choose(id => FeatureId.Parse(id).ToMaybe());

    private static Maybe<EmailAddress> Email(string? raw) => raw.ToMaybe().Map(Persisted.Rehydrate<EmailAddress, string>);
}
