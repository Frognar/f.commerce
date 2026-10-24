using Microsoft.AspNetCore.Mvc.ApplicationModels;

namespace FCommerce.Host.Localization;

/// <summary>Prefixes every Razor Page route with the culture: "/pl/…", "/en/…".</summary>
internal sealed class CultureRouteConvention : IPageRouteModelConvention
{
    public const string RouteKey = "culture";

    /// <summary>Pages reachable without a culture prefix (their culture comes from Accept-Language).</summary>
    private static readonly HashSet<string> CultureFreePages = new(StringComparer.OrdinalIgnoreCase) { "/Error" };

    public void Apply(PageRouteModel model)
    {
        if (CultureFreePages.Contains(model.ViewEnginePath))
        {
            return;
        }

        foreach (var selector in model.Selectors)
        {
            var template = selector.AttributeRouteModel?.Template ?? string.Empty;
            // Mutate in place to keep flags such as SuppressLinkGeneration ("/Index" vs "/").
            selector.AttributeRouteModel ??= new AttributeRouteModel();
            selector.AttributeRouteModel.Template = $"{{{RouteKey}:{CultureRouteConstraint.Name}}}/{template}".TrimEnd('/');
        }
    }
}

/// <summary>Matches only cultures supported by this instance.</summary>
internal sealed class CultureRouteConstraint(SupportedCultures cultures) : IRouteConstraint
{
    public const string Name = "culture";

    public bool Match(HttpContext? httpContext, IRouter? route, string routeKey, RouteValueDictionary values, RouteDirection routeDirection) =>
        values.TryGetValue(routeKey, out var value) && cultures.IsSupported(value?.ToString() ?? string.Empty);
}

internal static class CultureRedirect
{
    /// <summary>"/" → "/{best culture from Accept-Language}".</summary>
    public static IResult ToPreferredCulture(HttpRequest request, SupportedCultures cultures)
    {
        var languages = request.GetTypedHeaders().AcceptLanguage
            .Select(l => (l.Value.ToString(), l.Quality ?? 1.0));
        return Results.Redirect($"/{cultures.Negotiate(languages)}");
    }
}
