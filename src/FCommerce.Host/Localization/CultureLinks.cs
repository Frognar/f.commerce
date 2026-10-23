using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.TagHelpers;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace FCommerce.Host.Localization;

/// <summary>
/// Endpoint routing drops ambient route values (like {culture}) when a link targets a different page, so links and
/// form actions would lose the culture (an empty action posts to the current page instead). Every page link and form
/// therefore gets the current culture unless one is given explicitly.
/// </summary>
internal static class CultureLinks
{
    public static string CurrentCulture => CultureInfo.CurrentUICulture.Name;

    /// <summary>Url.Page with the current culture added.</summary>
    public static string? CulturePage(this IUrlHelper url, string pageName, object? values = null) =>
        url.Page(pageName, WithCulture(new RouteValueDictionary(values), CurrentCulture));

    /// <summary>Url.Page for a named page handler, with the current culture added.</summary>
    public static string? CulturePageHandler(this IUrlHelper url, string pageName, string handler, object? values = null) =>
        url.Page(pageName, handler, WithCulture(new RouteValueDictionary(values), CurrentCulture));

    /// <summary>The current page (route values and query string kept) in another culture — for hreflang and the language switcher.</summary>
    public static string? CurrentPageIn(this IUrlHelper url, ViewContext context, string culture)
    {
        var values = new RouteValueDictionary(context.RouteData.Values);
        values.Remove("page");
        foreach (var (key, value) in context.HttpContext.Request.Query)
        {
            values.TryAdd(key, value.ToString());
        }

        return url.Page(context.RouteData.Values["page"]?.ToString(), WithCulture(values, culture));
    }

    private static RouteValueDictionary WithCulture(RouteValueDictionary values, string culture)
    {
        values[CultureRouteConvention.RouteKey] = culture;
        return values;
    }
}

/// <summary>Replaces the framework's form tag helper: forms targeting another page keep the culture (see <see cref="CultureLinks"/>).</summary>
[HtmlTargetElement("form")]
public sealed class CultureFormTagHelper(IHtmlGenerator generator) : FormTagHelper(generator)
{
    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        if (Page is not null)
        {
            RouteValues.TryAdd(CultureRouteConvention.RouteKey, CultureLinks.CurrentCulture);
        }

        base.Process(context, output);
    }
}

/// <summary>Replaces the framework's anchor tag helper for <c>asp-page</c> links (see <see cref="CultureLinks"/>).</summary>
[HtmlTargetElement("a", Attributes = "asp-page")]
public sealed class CultureAnchorTagHelper(IHtmlGenerator generator) : AnchorTagHelper(generator)
{
    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        RouteValues.TryAdd(CultureRouteConvention.RouteKey, CultureLinks.CurrentCulture);
        base.Process(context, output);
    }
}
