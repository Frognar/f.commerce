using System.Collections.Immutable;
using System.Text.Json;
using FCommerce.Authorization.Core;
using FCommerce.Infrastructure.Serialization;
using FCommerce.Settings.Domain;
using FCommerce.SharedKernel.Contact;
using FCommerce.SharedKernel.Features;
using FCommerce.SharedKernel.Optional;

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
                settings.Branding.LogoImageId.OrNull(),
                settings.Contact.SupportEmail.Map(e => e.Value).OrNull(),
                settings.Contact.SupportPhone.OrNull(),
                settings.Contact.OperatorNotifications.Map(e => e.Value).OrNull(),
                settings.Commerce.DefaultPriceListCode.OrNull(),
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
            [.. document.EnabledFeatures.Choose(id => FeatureId.Parse(id).ToMaybe()).Where(available.Contains)],
            new Branding(document.StoreName, HexColor.Parse(document.AccentColor).ValueOr(_ => HexColor.DefaultAccent), document.LogoImageId.ToMaybe()),
            new SellerContact(Email(document.SupportEmail), document.SupportPhone.ToMaybe(), Email(document.OperatorNotifications)),
            new CommerceDefaults(document.DefaultPriceListCode.ToMaybe(), document.DefaultContactTemplate));
    }

    private static Maybe<EmailAddress> Email(string? raw) => raw.ToMaybe().Map(Persisted.Rehydrate<EmailAddress, string>);
}
