using System.Collections.Immutable;
using FCommerce.Authorization.Core;
using FCommerce.SharedKernel.Contact;
using FCommerce.SharedKernel.Features;
using FCommerce.SharedKernel.Results;
using FCommerce.SharedKernel.Validation;

namespace FCommerce.Settings.Domain;

/// <summary>A colour as "#rrggbb".</summary>
public sealed record HexColor
{
    private HexColor(string value) => Value = value;

    public string Value { get; }

    /// <summary>The platform's default accent.</summary>
    public static HexColor DefaultAccent { get; } = new("#1f5eff");

    public static Result<HexColor, Error> Parse(string? raw)
    {
        var value = raw?.Trim().ToLowerInvariant();
        return value is { Length: 7 } && value[0] == '#' && value[1..].All(char.IsAsciiHexDigitLower)
            ? new HexColor(value)
            : Error.Validation("color.invalid", "The colour must be in the form #rrggbb.");
    }

    public override string ToString() => Value;
}

/// <summary>How the storefront presents the seller.</summary>
public sealed record Branding(string StoreName, HexColor AccentColor, string? LogoImageId);

/// <summary>Who buyers and operators contact.</summary>
public sealed record SellerContact(EmailAddress? SupportEmail, string? SupportPhone, EmailAddress? OperatorNotifications);

/// <summary>Commercial defaults; the default price list is applied by pricing (prices are hidden without it).</summary>
public sealed record CommerceDefaults(string? DefaultPriceListCode, string DefaultContactTemplate);

/// <summary>Settings of one installation, edited by the seller in the back-office.</summary>
public sealed record PlatformSettings(
    long Version,
    CatalogVisibility CatalogVisibility,
    ImmutableHashSet<FeatureId> EnabledFeatures,
    Branding Branding,
    SellerContact Contact,
    CommerceDefaults Commerce)
{
    public const int MaxStoreName = 100;
    public const int MaxPhone = 40;
    public const int MaxPriceListCode = 64;

    /// <summary>Settings of a new installation: public catalogue, features on by default, neutral branding.</summary>
    public static PlatformSettings Defaults(ImmutableHashSet<FeatureId> available) =>
        new(
            0,
            CatalogVisibility.Public,
            [.. Feature.All.Where(f => f.EnabledByDefault && available.Contains(f.Id)).Select(f => f.Id)],
            new Branding("f.commerce", HexColor.DefaultAccent, null),
            new SellerContact(null, null, null),
            new CommerceDefaults(null, "buyer"));

    public FeatureSet Features(ImmutableHashSet<FeatureId> available) => new(available, EnabledFeatures);
}

/// <summary>Settings as submitted from the back-office form (primitives).</summary>
public sealed record UnvalidatedSettings(
    string? CatalogVisibility,
    IReadOnlyList<string>? EnabledFeatures,
    string? StoreName,
    string? AccentColor,
    string? LogoImageId,
    string? SupportEmail,
    string? SupportPhone,
    string? OperatorNotifications,
    string? DefaultPriceListCode,
    string? DefaultContactTemplate);

public static class SettingsRules
{
    private static readonly Func<CatalogVisibility, ImmutableHashSet<FeatureId>, Branding, SellerContact, CommerceDefaults, (CatalogVisibility, ImmutableHashSet<FeatureId>, Branding, SellerContact, CommerceDefaults)> Combine =
        (visibility, features, branding, contact, commerce) => (visibility, features, branding, contact, commerce);

    /// <summary>
    /// Validates the whole form at once. Features must be available in this installation and have their
    /// requirements enabled; the result gets the next version.
    /// </summary>
    public static Validated<PlatformSettings> Validate(UnvalidatedSettings input, ImmutableHashSet<FeatureId> available, long currentVersion) =>
        Combine
            .Apply(ParseVisibility(input.CatalogVisibility).ToValidated("catalogVisibility"))
            .Apply(ParseFeatures(input.EnabledFeatures, available).ToValidated("enabledFeatures"))
            .Apply(ParseBranding(input))
            .Apply(ParseContact(input))
            .Apply(ParseCommerce(input))
            .Map(parts => new PlatformSettings(currentVersion + 1, parts.Item1, parts.Item2, parts.Item3, parts.Item4, parts.Item5));

    /// <summary>Names of the settings that differ, for the change history.</summary>
    public static ImmutableArray<string> Changes(PlatformSettings before, PlatformSettings after) =>
    [
        .. before.CatalogVisibility != after.CatalogVisibility ? ["catalogVisibility"] : Array.Empty<string>(),
        .. after.EnabledFeatures.Except(before.EnabledFeatures).Select(f => $"feature+{f}"),
        .. before.EnabledFeatures.Except(after.EnabledFeatures).Select(f => $"feature-{f}"),
        .. before.Branding != after.Branding ? ["branding"] : Array.Empty<string>(),
        .. before.Contact != after.Contact ? ["contact"] : Array.Empty<string>(),
        .. before.Commerce != after.Commerce ? ["commerce"] : Array.Empty<string>(),
    ];

    private static Result<CatalogVisibility, Error> ParseVisibility(string? raw) =>
        Enum.TryParse<CatalogVisibility>(raw, ignoreCase: true, out var visibility) && Enum.IsDefined(visibility)
            ? visibility
            : Error.Validation("settings.catalog-visibility", "Choose who can see the catalogue.");

    private static Result<ImmutableHashSet<FeatureId>, Error> ParseFeatures(IReadOnlyList<string>? raw, ImmutableHashSet<FeatureId> available) =>
        (raw ?? []).Traverse(FeatureId.Parse)
            .Bind(ids => new FeatureSet(available, []).WithEnabled(ids))
            .Map(set => set.Enabled);

    private static Validated<Branding> ParseBranding(UnvalidatedSettings input) =>
        ((Func<string, HexColor, string?, Branding>)((name, color, logo) => new Branding(name, color, logo)))
            .Apply(Text(input.StoreName, PlatformSettings.MaxStoreName, required: true, "store-name").Map(n => n!).ToValidated("storeName"))
            .Apply(HexColor.Parse(input.AccentColor).ToValidated("accentColor"))
            .Apply(ImageId(input.LogoImageId).ToValidated("logoImageId"));

    private static Validated<SellerContact> ParseContact(UnvalidatedSettings input) =>
        ((Func<EmailAddress?, string?, EmailAddress?, SellerContact>)((support, phone, operators) => new SellerContact(support, phone, operators)))
            .Apply(OptionalEmail(input.SupportEmail).ToValidated("supportEmail"))
            .Apply(Text(input.SupportPhone, PlatformSettings.MaxPhone, required: false, "phone").ToValidated("supportPhone"))
            .Apply(OptionalEmail(input.OperatorNotifications).ToValidated("operatorNotifications"));

    private static Validated<CommerceDefaults> ParseCommerce(UnvalidatedSettings input) =>
        ((Func<string?, string, CommerceDefaults>)((priceList, template) => new CommerceDefaults(priceList, template)))
            .Apply(Text(input.DefaultPriceListCode, PlatformSettings.MaxPriceListCode, required: false, "price-list").ToValidated("defaultPriceListCode"))
            .Apply((input.DefaultContactTemplate is { } template && MembershipAttributes.Templates.ContainsKey(template)
                ? Result.Ok<string, Error>(template)
                : Error.Validation("settings.contact-template", "Choose a known attribute template.")).ToValidated("defaultContactTemplate"));

    private static Result<string?, Error> Text(string? raw, int maxLength, bool required, string code)
    {
        var value = string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
        return value switch
        {
            null when required => Error.Validation($"{code}.empty", "This value is required."),
            { Length: var length } when length > maxLength => Error.Validation($"{code}.too-long", $"At most {maxLength} characters."),
            _ when value?.Any(char.IsControl) == true => Error.Validation($"{code}.invalid", "Control characters are not allowed."),
            _ => value,
        };
    }

    private static Result<EmailAddress?, Error> OptionalEmail(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? Result.Ok<EmailAddress?, Error>(null) : EmailAddress.Parse(raw).Map(e => (EmailAddress?)e);

    private static Result<string?, Error> ImageId(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? Result.Ok<string?, Error>(null)
            : raw.Trim().ToLowerInvariant() is { Length: 64 } id && id.All(char.IsAsciiHexDigitLower)
                ? id
                : Error.Validation("image-id.invalid", "Unknown image.");
}
