using FCommerce.SharedKernel.Messaging;
using FCommerce.SharedKernel.Results;

namespace FCommerce.Settings.Contracts;

/// <summary>Current platform settings (served from memory; cheap to call on every request).</summary>
public sealed record GetPlatformSettings : IQuery<PlatformSettingsView>;

/// <param name="CatalogVisibility">"Public" or "SignedInOnly".</param>
/// <param name="Features">Features available in this installation, with their switch state.</param>
/// <param name="StockDisplay">"Levels" (available / low / out of stock) or "Exact" (quantities).</param>
/// <param name="LowStockThreshold">In "Levels", quantities up to this are shown as low.</param>
public sealed record PlatformSettingsView(
    long Version,
    string CatalogVisibility,
    IReadOnlyList<FeatureView> Features,
    BrandingView Branding,
    ContactView Contact,
    string? DefaultPriceListCode,
    string DefaultContactTemplate,
    string StockDisplay,
    decimal LowStockThreshold)
{
    public bool IsOn(string featureId) => Features.Any(f => f.Id == featureId && f.On);
}

/// <param name="Enabled">Switched on by the seller.</param>
/// <param name="On">Enabled and every required feature is on too (what the platform actually uses).</param>
public sealed record FeatureView(string Id, string Description, bool Enabled, bool On, IReadOnlyList<string> Requires);

public sealed record BrandingView(string StoreName, string AccentColor, string? LogoImageId);

public sealed record ContactView(string? SupportEmail, string? SupportPhone, string? OperatorNotifications);

/// <summary>
/// Replaces the settings. <see cref="Version"/> is the version the form was based on: if someone saved in the
/// meantime, the update fails with "settings.changed-meanwhile" instead of silently overwriting their changes.
/// </summary>
public sealed record UpdatePlatformSettings(
    Guid ActorId,
    long Version,
    string CatalogVisibility,
    IReadOnlyList<string> EnabledFeatures,
    string StoreName,
    string AccentColor,
    string? LogoImageId,
    string? SupportEmail,
    string? SupportPhone,
    string? OperatorNotifications,
    string? DefaultPriceListCode,
    string DefaultContactTemplate,
    string StockDisplay,
    decimal LowStockThreshold) : ICommand<Result<long, Error>>;

public sealed record GetSettingsHistory(int Take = 20) : IQuery<IReadOnlyList<SettingsChange>>;

/// <param name="Changes">What changed, e.g. "catalogVisibility", "feature+documents", "branding".</param>
public sealed record SettingsChange(long Version, DateTimeOffset At, Guid? ActorId, IReadOnlyList<string> Changes);

/// <summary>The settings changed. Published as "settings.platform-settings-changed.v1".</summary>
public sealed record PlatformSettingsChanged(long Version) : IIntegrationEvent;
