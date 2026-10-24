using System.Collections.Immutable;
using FCommerce.Authorization.Core;
using FCommerce.SharedKernel.Contact;
using FCommerce.SharedKernel.Features;
using FCommerce.SharedKernel.Optional;
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

    public static Result<HexColor, Error> Parse(string raw)
    {
        var value = raw.Trim().ToLowerInvariant();
        return value.Length == 7 && value[0] == '#' && value[1..].All(char.IsAsciiHexDigitLower)
            ? new HexColor(value)
            : Error.Validation("color.invalid", "The colour must be in the form #rrggbb.");
    }

    public override string ToString() => Value;
}

/// <summary>How the storefront presents the seller.</summary>
public sealed record Branding(string StoreName, HexColor AccentColor, Maybe<string> LogoImageId);

/// <summary>Who buyers and operators contact.</summary>
public sealed record SellerContact(Maybe<EmailAddress> SupportEmail, Maybe<string> SupportPhone, Maybe<EmailAddress> OperatorNotifications);

/// <summary>Commercial defaults; the default price list is applied by pricing (prices are hidden without it).</summary>
public sealed record CommerceDefaults(Maybe<string> DefaultPriceListCode, string DefaultContactTemplate);

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
            new Branding("f.commerce", HexColor.DefaultAccent, Maybe<string>.None),
            new SellerContact(Maybe<EmailAddress>.None, Maybe<string>.None, Maybe<EmailAddress>.None),
            new CommerceDefaults(Maybe<string>.None, "buyer"));

    public FeatureSet Features(ImmutableHashSet<FeatureId> available) => new(available, EnabledFeatures);
}

/// <summary>Settings as submitted from the back-office form (primitives; missing required texts arrive as empty).</summary>
public sealed record UnvalidatedSettings(
    string CatalogVisibility,
    IReadOnlyList<string> EnabledFeatures,
    string StoreName,
    string AccentColor,
    Maybe<string> LogoImageId,
    Maybe<string> SupportEmail,
    Maybe<string> SupportPhone,
    Maybe<string> OperatorNotifications,
    Maybe<string> DefaultPriceListCode,
    string DefaultContactTemplate);

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

    private static Result<CatalogVisibility, Error> ParseVisibility(string raw) =>
        Enum.TryParse<CatalogVisibility>(raw, ignoreCase: true, out var visibility) && Enum.IsDefined(visibility)
            ? visibility
            : Error.Validation("settings.catalog-visibility", "Choose who can see the catalogue.");

    private static Result<ImmutableHashSet<FeatureId>, Error> ParseFeatures(IReadOnlyList<string> raw, ImmutableHashSet<FeatureId> available) =>
        raw.Traverse(FeatureId.Parse)
            .Bind(ids => new FeatureSet(available, []).WithEnabled(ids))
            .Map(set => set.Enabled);

    private static Validated<Branding> ParseBranding(UnvalidatedSettings input) =>
        ((Func<string, HexColor, Maybe<string>, Branding>)((name, color, logo) => new Branding(name, color, logo)))
            .Apply(RequiredText(input.StoreName, PlatformSettings.MaxStoreName, "store-name").ToValidated("storeName"))
            .Apply(HexColor.Parse(input.AccentColor).ToValidated("accentColor"))
            .Apply(ImageId(input.LogoImageId).ToValidated("logoImageId"));

    private static Validated<SellerContact> ParseContact(UnvalidatedSettings input) =>
        ((Func<Maybe<EmailAddress>, Maybe<string>, Maybe<EmailAddress>, SellerContact>)((support, phone, operators) => new SellerContact(support, phone, operators)))
            .Apply(Maybe.ParseOptional(input.SupportEmail, EmailAddress.Parse).ToValidated("supportEmail"))
            .Apply(OptionalText(input.SupportPhone, PlatformSettings.MaxPhone, "phone").ToValidated("supportPhone"))
            .Apply(Maybe.ParseOptional(input.OperatorNotifications, EmailAddress.Parse).ToValidated("operatorNotifications"));

    private static Validated<CommerceDefaults> ParseCommerce(UnvalidatedSettings input) =>
        ((Func<Maybe<string>, string, CommerceDefaults>)((priceList, template) => new CommerceDefaults(priceList, template)))
            .Apply(OptionalText(input.DefaultPriceListCode, PlatformSettings.MaxPriceListCode, "price-list").ToValidated("defaultPriceListCode"))
            .Apply((MembershipAttributes.Templates.ContainsKey(input.DefaultContactTemplate)
                ? Result.Ok<string, Error>(input.DefaultContactTemplate)
                : Error.Validation("settings.contact-template", "Choose a known attribute template.")).ToValidated("defaultContactTemplate"));

    private static Result<string, Error> RequiredText(string raw, int maxLength, string code)
    {
        var value = raw.Trim();
        return value switch
        {
            "" => Error.Validation($"{code}.empty", "This value is required."),
            { Length: var length } when length > maxLength => Error.Validation($"{code}.too-long", $"At most {maxLength} characters."),
            _ when value.Any(char.IsControl) => Error.Validation($"{code}.invalid", "Control characters are not allowed."),
            _ => value,
        };
    }

    /// <summary>Blank means "not set".</summary>
    private static Result<Maybe<string>, Error> OptionalText(Maybe<string> raw, int maxLength, string code) =>
        Maybe.ParseOptional(raw, text => RequiredText(text, maxLength, code));

    private static Result<Maybe<string>, Error> ImageId(Maybe<string> raw) =>
        Maybe.ParseOptional(raw, text => text.Trim().ToLowerInvariant() is { Length: 64 } id && id.All(char.IsAsciiHexDigitLower)
            ? Result.Ok<string, Error>(id)
            : Error.Validation("image-id.invalid", "Unknown image."));
}
