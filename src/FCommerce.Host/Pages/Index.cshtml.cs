using System.Globalization;
using FCommerce.Application.EventStore;
using FCommerce.Host.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace FCommerce.Host.Pages;

public sealed class IndexModel(IEventStore eventStore, TimeProvider time, IStringLocalizer<SharedResource> localizer) : PageModel
{
    public void OnGet()
    {
    }

    /// <summary>htmx fragment: proves the request reaches the event store, rendered in the request culture.</summary>
    public async Task<IActionResult> OnGetStatusAsync(CancellationToken cancellationToken)
    {
        _ = await eventStore.ReadAllAsync(0, 1, cancellationToken);
        var message = localizer["StatusOk", time.GetLocalNow().ToString("F", CultureInfo.CurrentCulture)];
        return Content($"<p class=\"status-ok\">{System.Net.WebUtility.HtmlEncode(message)}</p>", "text/html");
    }
}
