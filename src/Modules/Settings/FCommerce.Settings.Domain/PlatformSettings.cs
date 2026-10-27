using System.Collections.Immutable;
using System.Globalization;
using FCommerce.Authorization.Core;
using FCommerce.SharedKernel.Contact;
using FCommerce.SharedKernel.Features;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Persistence;
using FCommerce.SharedKernel.Results;
using FCommerce.SharedKernel.Validation;

namespace FCommerce.Settings.Domain;

/// <summary>A colour as "#rrggbb".</summary>
public sealed record HexColor : IPersistedValue<HexColor, string>
{
    private HexColor(string value) => Value = value;

    public string Value { get; }

    /// <summary>The platform's default accent.</summary>
    public static HexColor DefaultAccent { get; } = new("#1f5eff");

    public static Result<HexColor, Error> Parse(string raw)
    {
        var value = raw.Trim().ToLowerInvariant();
        return IsWellFormed(value)
            ? new HexColor(value)
            : Error.Validation("color.invalid", "The colour must be in the form #rrggbb.");
    }

    private static bool IsWellFormed(string value) => value.Length == 7 && value[0] == '#' && value[1..].All(char.IsAsciiHexDigitLower);

    static bool IPersistedValue<HexColor, string>.IsWellFormed(string raw) => IsWellFormed(raw);

    static HexColor IPersistedValue<HexColor, string>.Rehydrate(string raw) => new(raw);

    string IPersistedValue<HexColor, string>.Dehydrate() => Value;

    public override string ToString() => Value;
}

/// <summary>The name of the store shown to buyers.</summary>
public sealed record StoreName : IPersistedValue<StoreName, string>
{
    public const int MaxLength = 100;

    private StoreName(string value) => Value = value;

    public string Value { get; }

    public static StoreName Default { get; } = new("f.commerce");

    public static Result<StoreName, Error> Parse(string raw) => SettingsText.Required(raw, MaxLength, "store-name").Map(value => new StoreName(value));

    static StoreName IPersistedValue<StoreName, string>.Rehydrate(string raw) => new(raw);

    string IPersistedValue<StoreName, string>.Dehydrate() => Value;

    public override string ToString() => Value;
}

/// <summary>A phone number to call the seller, as the seller writes it.</summary>
public sealed record Phone : IPersistedValue<Phone, string>
{
    public const int MaxLength = 40;

    private Phone(string value) => Value = value;

    public string Value { get; }

    public static Result<Phone, Error> Parse(string raw) => SettingsText.Required(raw, MaxLength, "phone").Map(value => new Phone(value));

    static Phone IPersistedValue<Phone, string>.Rehydrate(string raw) => new(raw);

    string IPersistedValue<Phone, string>.Dehydrate() => Value;

    public override string ToString() => Value;
}

/// <summary>The code of an ERP price list (whether the list exists is known to pricing, not here).</summary>
public sealed record PriceListReference : IPersistedValue<PriceListReference, string>
{
    public const int MaxLength = 64;

    private PriceListReference(string value) => Value = value;

    public string Value { get; }

    public static Result<PriceListReference, Error> Parse(string raw) =>
        SettingsText.Required(raw, MaxLength, "price-list").Map(value => new PriceListReference(value));

    static PriceListReference IPersistedValue<PriceListReference, string>.Rehydrate(string raw) => new(raw);

    string IPersistedValue<PriceListReference, string>.Dehydrate() => Value;

    public override string ToString() => Value;
}

/// <summary>The rights template contacts are invited with when the ERP names none: one the platform knows.</summary>
public sealed record ContactTemplate : IPersistedValue<ContactTemplate, string>
{
    private ContactTemplate(string value) => Value = value;

    public string Value { get; }

    public static ContactTemplate Default { get; } = new("buyer");

    public static Result<ContactTemplate, Error> Parse(string raw) =>
        MembershipAttributes.Templates.ContainsKey(raw)
            ? new ContactTemplate(raw)
            : Error.Validation("settings.contact-template", "Choose a known attribute template.");

    static ContactTemplate IPersistedValue<ContactTemplate, string>.Rehydrate(string raw) => new(raw);

    string IPersistedValue<ContactTemplate, string>.Dehydrate() => Value;

    public override string ToString() => Value;
}

/// <summary>The logo: an image of the media store (its 64 hex digit id).</summary>
public sealed record LogoImage : IPersistedValue<LogoImage, string>
{
    private LogoImage(string id) => Id = id;

    public string Id { get; }

    public static Result<LogoImage, Error> Parse(string raw) =>
        raw.Trim().ToLowerInvariant() is { Length: 64 } id && id.All(char.IsAsciiHexDigitLower)
            ? new LogoImage(id)
            : Error.Validation("image-id.invalid", "Unknown image.");

    static LogoImage IPersistedValue<LogoImage, string>.Rehydrate(string raw) => new(raw);

    string IPersistedValue<LogoImage, string>.Dehydrate() => Id;

    public override string ToString() => Id;
}

/// <summary>Quantities up to this are shown as "low": zero or more, at most 3 decimal places, fits numeric(15,3).</summary>
public sealed record LowStockThreshold : IPersistedValue<LowStockThreshold, decimal>
{
    public const int Decimals = 3;
    public const decimal Max = 999_999_999_999.999m;

    private LowStockThreshold(decimal value) => Value = value;

    public decimal Value { get; }

    public static LowStockThreshold Default { get; } = new(10m);

    public static Result<LowStockThreshold, Error> Parse(decimal raw) =>
        raw is >= 0 and <= Max && decimal.Round(raw, Decimals) == raw
            ? new LowStockThreshold(raw)
            : Error.Validation("settings.low-stock-threshold", "The threshold must be zero or more, with at most 3 decimal places.");

    static bool IPersistedValue<LowStockThreshold, decimal>.IsWellFormed(decimal raw) => raw >= 0;

    static LowStockThreshold IPersistedValue<LowStockThreshold, decimal>.Rehydrate(decimal raw) => new(raw);

    decimal IPersistedValue<LowStockThreshold, decimal>.Dehydrate() => Value;

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// The features the seller switched on: all available in this installation, each with the features it requires.
/// </summary>
public sealed record EnabledFeatures
{
    private EnabledFeatures(ImmutableHashSet<FeatureId> ids) => Ids = ids;

    public ImmutableHashSet<FeatureId> Ids { get; }

    /// <summary>The features on by default that are available here.</summary>
    public static EnabledFeatures Defaults(ImmutableHashSet<FeatureId> available) =>
        Restore([], [], available);

    /// <summary>The seller's choice: only available features, with their requirements chosen too.</summary>
    public static Result<EnabledFeatures, Error> Choose(IEnumerable<FeatureId> chosen, ImmutableHashSet<FeatureId> available) =>
        new FeatureSet(available, []).WithEnabled(chosen).Map(set => new EnabledFeatures(set.Enabled));

    /// <summary>
    /// The choice saved earlier, in today's installation. Features no longer available are off, and so are the
    /// features that require them. Features added to the catalogue after saving (not in <paramref name="known"/>)
    /// could not have been switched off by the seller: those on by default are on, if their requirements are.
    /// Features known at saving keep the seller's choice.
    /// </summary>
    public static EnabledFeatures Restore(IEnumerable<FeatureId> saved, ImmutableHashSet<FeatureId> known, ImmutableHashSet<FeatureId> available)
    {
        var enabled = Feature.All
            .Where(f => available.Contains(f.Id))
            .Aggregate(
                saved.Where(available.Contains).ToImmutableHashSet(),
                (result, feature) => feature.EnabledByDefault && !known.Contains(feature.Id) && feature.RequiresAll.All(result.Contains) ? result.Add(feature.Id) : result);
        return new EnabledFeatures(WithRequirements(enabled));
    }

    public bool Contains(FeatureId feature) => Ids.Contains(feature);

    /// <summary>Only the features whose requirements are in the set too (requirements may require others in turn).</summary>
    private static ImmutableHashSet<FeatureId> WithRequirements(ImmutableHashSet<FeatureId> features)
    {
        var kept = features.Where(id => Feature.All.Single(f => f.Id == id).RequiresAll.All(features.Contains)).ToImmutableHashSet();
        return kept.Count == features.Count ? kept : WithRequirements(kept);
    }
}

/// <summary>How the storefront presents the seller.</summary>
public sealed record Branding(StoreName StoreName, HexColor AccentColor, Maybe<LogoImage> Logo);

/// <summary>Who buyers and operators contact.</summary>
public sealed record SellerContact(Maybe<EmailAddress> SupportEmail, Maybe<Phone> SupportPhone, Maybe<EmailAddress> OperatorNotifications);

/// <summary>
/// Commercial defaults; pricing applies the default price list to customers without their own and, with the
/// "public-prices" feature on, to visitors who are not signed in.
/// </summary>
public sealed record CommerceDefaults(Maybe<PriceListReference> DefaultPriceList, ContactTemplate DefaultContactTemplate);

/// <summary>How stock is shown: exact quantities, or levels (available / low / out of stock).</summary>
public enum StockDisplayMode
{
    Levels,
    Exact,
}

/// <param name="LowThreshold">Quantities up to this have the "low" status, in both modes.</param>
public sealed record StockDisplay(StockDisplayMode Mode, LowStockThreshold LowThreshold)
{
    public static StockDisplay Default { get; } = new(StockDisplayMode.Levels, LowStockThreshold.Default);
}

/// <summary>
/// Settings of one installation, edited by the seller in the back-office. Made only as the defaults of a new
/// installation or by <see cref="SettingsRules.Update"/>, which gives each save the next version.
/// </summary>
public sealed record PlatformSettings : IPersistedValue<PlatformSettings, PlatformSettings.Stored>
{
    private PlatformSettings(Stored stored) => Value = stored;

    public long Version => Value.Version;

    public CatalogVisibility CatalogVisibility => Value.CatalogVisibility;

    public EnabledFeatures EnabledFeatures => Value.EnabledFeatures;

    public Branding Branding => Value.Branding;

    public SellerContact Contact => Value.Contact;

    public CommerceDefaults Commerce => Value.Commerce;

    public StockDisplay Stock => Value.Stock;

    private Stored Value { get; }

    /// <summary>Settings of a new installation: public catalogue, features on by default, neutral branding.</summary>
    public static PlatformSettings Defaults(ImmutableHashSet<FeatureId> available) =>
        new(new Stored(
            0,
            CatalogVisibility.Public,
            EnabledFeatures.Defaults(available),
            new Branding(StoreName.Default, HexColor.DefaultAccent, Maybe<LogoImage>.None),
            new SellerContact(Maybe<EmailAddress>.None, Maybe<Phone>.None, Maybe<EmailAddress>.None),
            new CommerceDefaults(Maybe<PriceListReference>.None, ContactTemplate.Default),
            StockDisplay.Default));

    public FeatureSet Features(ImmutableHashSet<FeatureId> available) => new(available, EnabledFeatures.Ids);

    internal PlatformSettings Next(Stored content) => new(content with { Version = Version + 1 });

    /// <summary>The stored form; the version is whatever was saved (never negative).</summary>
    [RawInput]
    public sealed record Stored(
        long Version,
        CatalogVisibility CatalogVisibility,
        EnabledFeatures EnabledFeatures,
        Branding Branding,
        SellerContact Contact,
        CommerceDefaults Commerce,
        StockDisplay Stock);

    static bool IPersistedValue<PlatformSettings, Stored>.IsWellFormed(Stored raw) => raw.Version >= 0;

    static PlatformSettings IPersistedValue<PlatformSettings, Stored>.Rehydrate(Stored raw) => new(raw);

    Stored IPersistedValue<PlatformSettings, Stored>.Dehydrate() => Value;
}

/// <summary>Settings as submitted from the back-office form (primitives; missing required texts arrive as empty).</summary>
[RawInput]
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
    string DefaultContactTemplate,
    string StockDisplay,
    decimal LowStockThreshold);

public static class SettingsRules
{
    /// <summary>
    /// A save of the form over <paramref name="current"/>, made by someone who saw <paramref name="expectedVersion"/>:
    /// refused when the settings were saved by someone else meanwhile, otherwise validated as a whole.
    /// </summary>
    public static Result<PlatformSettings, Error> Update(PlatformSettings current, long expectedVersion, UnvalidatedSettings input, ImmutableHashSet<FeatureId> available) =>
        expectedVersion == current.Version
            ? Validate(input, available, current).ToResult()
            : ChangedMeanwhile;

    /// <summary>The settings were saved by someone else since the editor read them.</summary>
    public static Error ChangedMeanwhile { get; } =
        Error.Conflict("settings.changed-meanwhile", "The settings were changed by someone else in the meantime. Reload and try again.");

    /// <summary>
    /// Validates the whole form at once. Features must be available in this installation and have their
    /// requirements enabled; the result is the version after <paramref name="current"/>.
    /// </summary>
    public static Validated<PlatformSettings> Validate(UnvalidatedSettings input, ImmutableHashSet<FeatureId> available, PlatformSettings current) =>
        ((Func<CatalogVisibility, EnabledFeatures, Branding, SellerContact, CommerceDefaults, StockDisplay, PlatformSettings>)(
            (visibility, features, branding, contact, commerce, stock) =>
                current.Next(new PlatformSettings.Stored(current.Version, visibility, features, branding, contact, commerce, stock))))
            .Apply(ParseVisibility(input.CatalogVisibility).ToValidated("catalogVisibility"))
            .Apply(input.EnabledFeatures.Traverse(FeatureId.Parse).Bind(ids => EnabledFeatures.Choose(ids, available)).ToValidated("enabledFeatures"))
            .Apply(ParseBranding(input))
            .Apply(ParseContact(input))
            .Apply(ParseCommerce(input))
            .Apply(ParseStock(input));

    /// <summary>Names of the settings that differ, for the change history.</summary>
    public static ImmutableArray<string> Changes(PlatformSettings before, PlatformSettings after) =>
    [
        .. before.CatalogVisibility != after.CatalogVisibility ? ["catalogVisibility"] : Array.Empty<string>(),
        .. after.EnabledFeatures.Ids.Except(before.EnabledFeatures.Ids).Select(f => $"feature+{f}"),
        .. before.EnabledFeatures.Ids.Except(after.EnabledFeatures.Ids).Select(f => $"feature-{f}"),
        .. before.Branding != after.Branding ? ["branding"] : Array.Empty<string>(),
        .. before.Contact != after.Contact ? ["contact"] : Array.Empty<string>(),
        .. before.Commerce != after.Commerce ? ["commerce"] : Array.Empty<string>(),
        .. before.Stock != after.Stock ? ["stock"] : Array.Empty<string>(),
    ];

    private static Result<CatalogVisibility, Error> ParseVisibility(string raw) =>
        Enums.Parse<CatalogVisibility>(raw, Error.Validation("settings.catalog-visibility", "Choose who can see the catalogue."));

    private static Validated<Branding> ParseBranding(UnvalidatedSettings input) =>
        ((Func<StoreName, HexColor, Maybe<LogoImage>, Branding>)((name, color, logo) => new Branding(name, color, logo)))
            .Apply(StoreName.Parse(input.StoreName).ToValidated("storeName"))
            .Apply(HexColor.Parse(input.AccentColor).ToValidated("accentColor"))
            .Apply(Maybe.ParseOptional(input.LogoImageId, LogoImage.Parse).ToValidated("logoImageId"));

    private static Validated<SellerContact> ParseContact(UnvalidatedSettings input) =>
        ((Func<Maybe<EmailAddress>, Maybe<Phone>, Maybe<EmailAddress>, SellerContact>)((support, phone, operators) => new SellerContact(support, phone, operators)))
            .Apply(Maybe.ParseOptional(input.SupportEmail, EmailAddress.Parse).ToValidated("supportEmail"))
            .Apply(Maybe.ParseOptional(input.SupportPhone, Phone.Parse).ToValidated("supportPhone"))
            .Apply(Maybe.ParseOptional(input.OperatorNotifications, EmailAddress.Parse).ToValidated("operatorNotifications"));

    private static Validated<CommerceDefaults> ParseCommerce(UnvalidatedSettings input) =>
        ((Func<Maybe<PriceListReference>, ContactTemplate, CommerceDefaults>)((priceList, template) => new CommerceDefaults(priceList, template)))
            .Apply(Maybe.ParseOptional(input.DefaultPriceListCode, PriceListReference.Parse).ToValidated("defaultPriceListCode"))
            .Apply(ContactTemplate.Parse(input.DefaultContactTemplate).ToValidated("defaultContactTemplate"));

    private static Validated<StockDisplay> ParseStock(UnvalidatedSettings input) =>
        ((Func<StockDisplayMode, LowStockThreshold, StockDisplay>)((mode, threshold) => new StockDisplay(mode, threshold)))
            .Apply(Enums.Parse<StockDisplayMode>(input.StockDisplay, Error.Validation("settings.stock-display", "Choose how stock is shown.")).ToValidated("stockDisplay"))
            .Apply(LowStockThreshold.Parse(input.LowStockThreshold).ToValidated("lowStockThreshold"));
}

/// <summary>Texts the seller types: trimmed, required, bounded, no control characters.</summary>
internal static class SettingsText
{
    public static Result<string, Error> Required(string raw, int maxLength, string code)
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
}
