using System.Globalization;
using FCommerce.SharedKernel.Localization;
using FCommerce.SharedKernel.Results;
using Microsoft.Extensions.Options;

namespace FCommerce.Host.Localization;

/// <summary>Raw configuration section "Localization", validated into <see cref="SupportedCultures"/> on startup.</summary>
public sealed class LocalizationSettings
{
    public const string Section = "Localization";

    public string DefaultCulture { get; set; } = "pl";

    public IList<string> SupportedCultures { get; init; } = [];
}

/// <summary>Cultures this instance serves; the default culture is always among them.</summary>
public sealed record SupportedCultures(CultureCode Default, IReadOnlyList<CultureCode> All)
{
    public static Result<SupportedCultures, Error> From(LocalizationSettings settings) =>
        from defaultCulture in CultureCode.Parse(settings.DefaultCulture)
        from others in settings.SupportedCultures.Traverse(CultureCode.Parse)
        select new SupportedCultures(defaultCulture, [.. others.Prepend(defaultCulture).Distinct()]);

    public bool IsSupported(string? value) =>
        CultureCode.Parse(value).Match(All.Contains, _ => false);

    /// <summary>Best supported culture for Accept-Language entries; the default culture when none matches.</summary>
    public CultureCode Negotiate(IEnumerable<(string Value, double Quality)> acceptLanguages) =>
        acceptLanguages
            .OrderByDescending(l => l.Quality)
            .SelectMany(l => CultureCode.Parse(l.Value).Match(c => (CultureCode[])[c, c.Language], _ => []))
            .FirstOrDefault(All.Contains)
        ?? Default;

    public IList<CultureInfo> CultureInfos => [.. All.Select(c => CultureInfo.GetCultureInfo(c.Value))];
}

internal sealed class LocalizationSettingsValidator : IValidateOptions<LocalizationSettings>
{
    public ValidateOptionsResult Validate(string? name, LocalizationSettings options) =>
        SupportedCultures.From(options).Match(
            _ => ValidateOptionsResult.Success,
            error => ValidateOptionsResult.Fail($"{LocalizationSettings.Section}: {error.Message}"));
}
