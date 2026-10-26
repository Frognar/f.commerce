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
/// Storage shape of <see cref="PlatformSettings"/>; reading it back trusts what was validated when saved. Fields added
/// later are at the end and missing in older documents (read as defaults).
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
                [.. settings.EnabledFeatures.Select(f => f.Value).Order(StringComparer.Ordinal)],
                settings.Branding.StoreName,
                settings.Branding.AccentColor.Value,
                settings.Branding.LogoImageId.OrNull(),
                settings.Contact.SupportEmail.Map(e => e.Value).OrNull(),
                settings.Contact.SupportPhone.OrNull(),
                settings.Contact.OperatorNotifications.Map(e => e.Value).OrNull(),
                settings.Commerce.DefaultPriceListCode.OrNull(),
                settings.Commerce.DefaultContactTemplate,
                settings.Stock.Mode.ToString(),
                settings.Stock.LowThreshold,
                [.. Feature.All.Select(f => f.Id.Value).Order(StringComparer.Ordinal)]),
            PersistenceJson.Options);

    /// <summary>
    /// Features no longer known to the code or no longer available in this installation are dropped (they are off);
    /// features added to the code since saving are on when on by default.
    /// </summary>
    public static PlatformSettings Read(string json, long version, ImmutableHashSet<FeatureId> available)
    {
        var document = JsonSerializer.Deserialize<SettingsDocument>(json, PersistenceJson.Options)
            ?? throw new JsonException("The settings document is empty.");
        var enabled = Features(document.EnabledFeatures).Where(available.Contains).ToImmutableHashSet();
        var known = Features(document.KnownFeatures ?? FirstFeatures).ToImmutableHashSet();
        return new PlatformSettings(
            version,
            Stored.Enum<CatalogVisibility>(document.CatalogVisibility, "catalog visibility"),
            SettingsRules.WithNewFeatures(enabled, known, available),
            new Branding(document.StoreName, HexColor.Parse(document.AccentColor).ValueOr(_ => HexColor.DefaultAccent), document.LogoImageId.ToMaybe()),
            new SellerContact(Email(document.SupportEmail), document.SupportPhone.ToMaybe(), Email(document.OperatorNotifications)),
            new CommerceDefaults(document.DefaultPriceListCode.ToMaybe(), document.DefaultContactTemplate),
            new Domain.StockDisplay(
                document.StockDisplay.ToMaybe().Map(mode => Stored.Enum<StockDisplayMode>(mode, "stock display")).ValueOr(Domain.StockDisplay.Default.Mode),
                document.LowStockThreshold.ToMaybe().ValueOr(Domain.StockDisplay.Default.LowThreshold)));
    }

    private static IEnumerable<FeatureId> Features(IEnumerable<string> ids) => ids.Choose(id => FeatureId.Parse(id).ToMaybe());

    private static Maybe<EmailAddress> Email(string? raw) => raw.ToMaybe().Map(Persisted.Rehydrate<EmailAddress, string>);
}
