using System.Globalization;
using FCommerce.Access.Contracts;
using FCommerce.Application.Messaging;
using FCommerce.Application.Results;
using FCommerce.Host.Http;
using FCommerce.Host.Localization;
using FCommerce.Host.Security;
using FCommerce.Media.Contracts;
using FCommerce.Settings.Contracts;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Results;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace FCommerce.Host.Pages.Admin;

public sealed class SettingsModel(
    CurrentUser user,
    IQueryHandler<GetPlatformSettings, PlatformSettingsView> settings,
    ICommandHandler<UpdatePlatformSettings, Result<long, Error>> update,
    IQueryHandler<GetSettingsHistory, IReadOnlyList<SettingsChange>> history,
    IQueryHandler<GetOperators, IReadOnlyList<OperatorSummary>> operators,
    ICommandHandler<UploadImage, Result<UploadedImage, Error>> uploadImage,
    IStringLocalizer<SharedResource> texts) : PageModel
{
    public const long MaxLogoBytes = 5 * 1024 * 1024;

    /// <summary>The form as posted (optional fields arrive empty or missing).</summary>
    [NullableEdge]
    public sealed class SettingsForm
    {
        public long Version { get; set; }

        public string CatalogVisibility { get; set; } = "Public";

        public List<string> EnabledFeatures { get; set; } = [];

        public string StoreName { get; set; } = string.Empty;

        public string AccentColor { get; set; } = "#1f5eff";

        public string? LogoImageId { get; set; }

        public bool RemoveLogo { get; set; }

        public string? SupportEmail { get; set; }

        public string? SupportPhone { get; set; }

        public string? OperatorNotifications { get; set; }

        public string? DefaultPriceListCode { get; set; }

        public string DefaultContactTemplate { get; set; } = "buyer";

        public string StockDisplay { get; set; } = "Levels";

        /// <summary>Text, so that both "2,5" and "2.5" are accepted whatever the page culture.</summary>
        public string LowStockThreshold { get; set; } = "10";
    }

    [BindProperty]
    public SettingsForm Form { get; set; } = new();

    [BindProperty]
    public IFormFile? Logo { get; set; }

    private Maybe<PlatformSettingsView> current;

    /// <summary>The stored settings; loaded by every handler before the page renders.</summary>
    public PlatformSettingsView Current => current.Match(c => c, () => throw new InvalidOperationException("The settings are not loaded."));

    public IReadOnlyList<SettingsChange> History { get; private set; } = [];

    public IReadOnlyDictionary<Guid, string> OperatorNames { get; private set; } = new Dictionary<Guid, string>();

    public Maybe<string> Saved { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
        Form = new SettingsForm
        {
            Version = Current.Version,
            CatalogVisibility = Current.CatalogVisibility,
            EnabledFeatures = [.. Current.Features.Where(f => f.Enabled).Select(f => f.Id)],
            StoreName = Current.Branding.StoreName,
            AccentColor = Current.Branding.AccentColor,
            LogoImageId = Current.Branding.LogoImageId,
            SupportEmail = Current.Contact.SupportEmail,
            SupportPhone = Current.Contact.SupportPhone,
            OperatorNotifications = Current.Contact.OperatorNotifications,
            DefaultPriceListCode = Current.DefaultPriceListCode,
            DefaultContactTemplate = Current.DefaultContactTemplate,
            StockDisplay = Current.StockDisplay,
            LowStockThreshold = Current.LowStockThreshold.ToString("0.###", CultureInfo.CurrentCulture),
        };
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        var threshold = Quantity(Form.LowStockThreshold);
        var logo = await LogoAsync(cancellationToken);
        var saved = await threshold.Bind(t => logo.Map(l => (Threshold: t, LogoId: l))).BindAsync(form => update.HandleAsync(
            new UpdatePlatformSettings(
                user.RequiredUserId, Form.Version, Form.CatalogVisibility, Form.EnabledFeatures, Form.StoreName, Form.AccentColor, form.LogoId.OrNull(),
                Form.SupportEmail, Form.SupportPhone, Form.OperatorNotifications, Form.DefaultPriceListCode, Form.DefaultContactTemplate,
                Form.StockDisplay, form.Threshold),
            cancellationToken));

        if (saved.Match(_ => true, error => { ModelState.AddErrors(error, texts); return false; }))
        {
            ModelState.Clear();
            await OnGetAsync(cancellationToken);
            Saved = Maybe.Some<string>(texts["Saved"]);
            return Page();
        }

        await LoadAsync(cancellationToken);
        return Page();
    }

    /// <summary>The logo to keep: a newly uploaded one, none (removed) or the current one.</summary>
    private async Task<Result<Maybe<string>, Error>> LogoAsync(CancellationToken cancellationToken)
    {
        if (Form.RemoveLogo)
        {
            return Result.Ok<Maybe<string>, Error>(Maybe<string>.None);
        }

        return await Logo.ToMaybe().Where(file => file.Length > 0).Match(
            UploadAsync,
            () => Task.FromResult(Result.Ok<Maybe<string>, Error>(Form.LogoImageId.ToMaybe())));

        async Task<Result<Maybe<string>, Error>> UploadAsync(IFormFile file)
        {
            if (file.Length > MaxLogoBytes)
            {
                return Error.Validation("image.too-large", "The logo must not exceed 5 MB.") with { Field = "Logo" };
            }

            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer, cancellationToken);
            return (await uploadImage.HandleAsync(new UploadImage(buffer.ToArray()), cancellationToken)).Map(image => Maybe.Some(image.Id));
        }
    }

    /// <summary>A quantity typed with a decimal comma or point; its range is checked by the settings rules.</summary>
    private static Result<decimal, Error> Quantity(string raw) =>
        decimal.TryParse(
            raw.Trim().Replace(',', '.'),
            NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite | NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture,
            out var quantity)
            ? quantity
            : Error.Validation("settings.low-stock-threshold", "The threshold must be a number.") with { Field = "lowStockThreshold" };

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        current = await settings.HandleAsync(new GetPlatformSettings(), cancellationToken);
        History = await history.HandleAsync(new GetSettingsHistory(), cancellationToken);
        OperatorNames = (await operators.HandleAsync(new GetOperators(), cancellationToken)).ToDictionary(o => o.UserId, o => o.Name);
    }

    /// <summary>A history entry in words: "feature+documents" → "Włączono: Dokumenty".</summary>
    public string Describe(string change) =>
        change switch
        {
            _ when change.StartsWith("feature+", StringComparison.Ordinal) => texts["Change.FeatureOn", texts[$"Feature.{change[8..]}"]],
            _ when change.StartsWith("feature-", StringComparison.Ordinal) => texts["Change.FeatureOff", texts[$"Feature.{change[8..]}"]],
            _ => texts[$"Change.{change}"],
        };
}
