using FCommerce.Authorization.Core;
using FCommerce.SharedKernel.Features;

namespace FCommerce.Host.Security;

/// <summary>Instance-wide switches the access policies depend on.</summary>
public interface IPlatformState
{
    FeatureSet Features { get; }

    CatalogVisibility CatalogVisibility { get; }
}

/// <summary>
/// Reads the switches from configuration ("Features:Available", "Storefront:CatalogVisibility"); every available feature
/// is enabled. Replaced by settings edited in the back-office.
/// </summary>
internal sealed class ConfiguredPlatformState(IConfiguration configuration) : IPlatformState
{
    public FeatureSet Features { get; } = Read(configuration);

    public CatalogVisibility CatalogVisibility { get; } =
        Enum.TryParse<CatalogVisibility>(configuration["Storefront:CatalogVisibility"], ignoreCase: true, out var visibility) ? visibility : CatalogVisibility.Public;

    private static FeatureSet Read(IConfiguration configuration)
    {
        var listed = configuration.GetSection("Features:Available").Get<string[]>();
        var available = listed is null
            ? Feature.All.Where(f => f.EnabledByDefault).Select(f => f.Id).ToHashSet()
            : listed.Select(id => FeatureId.Parse(id).Match(f => f, e => throw new InvalidOperationException($"Features:Available: {e.Message}"))).ToHashSet();
        return new FeatureSet([.. available], [.. available]);
    }
}
