using System.Collections.Immutable;
using System.Text.Json;
using FCommerce.Authorization.Core;
using FCommerce.Infrastructure.Serialization;
using FCommerce.Settings.Domain;
using FCommerce.SharedKernel.Contact;
using FCommerce.SharedKernel.Features;

namespace FCommerce.Settings.Persistence;

/// <summary>Storage shape of <see cref="PlatformSettings"/>; reading it back trusts what was validated when saved.</summary>
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
    string DefaultContactTemplate)
{
    public static string Write(PlatformSettings settings) =>
        JsonSerializer.Serialize(
            new SettingsDocument(
                settings.CatalogVisibility.ToString(),
                [.. settings.EnabledFeatures.Select(f => f.Value).Order(StringComparer.Ordinal)],
                settings.Branding.StoreName,
                settings.Branding.AccentColor.Value,
                settings.Branding.LogoImageId,
                settings.Contact.SupportEmail?.Value,
                settings.Contact.SupportPhone,
                settings.Contact.OperatorNotifications?.Value,
                settings.Commerce.DefaultPriceListCode,
                settings.Commerce.DefaultContactTemplate),
            PersistenceJson.Options);

    /// <summary>
    /// Features no longer known to the code or no longer available in this installation are dropped (they are off).
    /// </summary>
    public static PlatformSettings Read(string json, long version, ImmutableHashSet<FeatureId> available)
    {
        var document = JsonSerializer.Deserialize<SettingsDocument>(json, PersistenceJson.Options)
            ?? throw new JsonException("The settings document is empty.");
        return new PlatformSettings(
            version,
            Enum.Parse<CatalogVisibility>(document.CatalogVisibility),
            [.. document.EnabledFeatures.Select(id => FeatureId.Parse(id).Match(f => (FeatureId?)f, _ => null)).OfType<FeatureId>().Where(available.Contains)],
            new Branding(document.StoreName, HexColor.Parse(document.AccentColor).Match(c => c, _ => HexColor.DefaultAccent), document.LogoImageId),
            new SellerContact(Email(document.SupportEmail), document.SupportPhone, Email(document.OperatorNotifications)),
            new CommerceDefaults(document.DefaultPriceListCode, document.DefaultContactTemplate));
    }

    private static EmailAddress? Email(string? raw) => raw is null ? null : Persisted.Rehydrate<EmailAddress, string>(raw);
}
