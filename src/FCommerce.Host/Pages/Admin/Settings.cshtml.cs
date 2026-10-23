using FCommerce.Access.Contracts;
using FCommerce.Application.Messaging;
using FCommerce.Application.Results;
using FCommerce.Host.Http;
using FCommerce.Host.Localization;
using FCommerce.Host.Security;
using FCommerce.Media.Contracts;
using FCommerce.Settings.Contracts;
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
    }

    [BindProperty]
    public SettingsForm Form { get; set; } = new();

    [BindProperty]
    public IFormFile? Logo { get; set; }

    public PlatformSettingsView Current { get; private set; } = null!;

    public IReadOnlyList<SettingsChange> History { get; private set; } = [];

    public IReadOnlyDictionary<Guid, string> OperatorNames { get; private set; } = new Dictionary<Guid, string>();

    public string? Saved { get; private set; }

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
        };
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        var logo = await LogoAsync(cancellationToken);
        var saved = await logo.BindAsync(logoId => update.HandleAsync(
            new UpdatePlatformSettings(
                user.UserId!.Value, Form.Version, Form.CatalogVisibility, Form.EnabledFeatures, Form.StoreName, Form.AccentColor, logoId,
                Form.SupportEmail, Form.SupportPhone, Form.OperatorNotifications, Form.DefaultPriceListCode, Form.DefaultContactTemplate),
            cancellationToken));

        if (saved.Match(_ => true, error => { ModelState.AddErrors(error, texts); return false; }))
        {
            ModelState.Clear();
            await OnGetAsync(cancellationToken);
            Saved = texts["Saved"];
            return Page();
        }

        await LoadAsync(cancellationToken);
        return Page();
    }

    /// <summary>The logo to keep: a newly uploaded one, none (removed) or the current one.</summary>
    private async Task<Result<string?, Error>> LogoAsync(CancellationToken cancellationToken)
    {
        if (Form.RemoveLogo)
        {
            return Result.Ok<string?, Error>(null);
        }

        if (Logo is null || Logo.Length == 0)
        {
            return Form.LogoImageId;
        }

        if (Logo.Length > MaxLogoBytes)
        {
            return Error.Validation("image.too-large", "The logo must not exceed 5 MB.") with { Field = "Logo" };
        }

        using var buffer = new MemoryStream();
        await Logo.CopyToAsync(buffer, cancellationToken);
        return (await uploadImage.HandleAsync(new UploadImage(buffer.ToArray()), cancellationToken)).Map(image => (string?)image.Id);
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        Current = await settings.HandleAsync(new GetPlatformSettings(), cancellationToken);
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
