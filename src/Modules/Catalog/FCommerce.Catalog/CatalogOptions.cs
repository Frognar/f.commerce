using FCommerce.SharedKernel.Collections;
using FCommerce.SharedKernel.Localization;
using FCommerce.SharedKernel.Results;

namespace FCommerce.Catalog;

/// <summary>Instance settings the catalog needs from the host.</summary>
public sealed class CatalogOptions
{
    /// <summary>Cultures the storefront serves; the search index keeps one copy of every product per culture.</summary>
    public IList<string> Cultures { get; } = [];

    /// <summary>Culture used when a product has no text in the requested one.</summary>
    public string DefaultCulture { get; set; } = "pl";
}

internal sealed record CatalogCultures(CultureCode Default, EquatableArray<CultureCode> All)
{
    public static Result<CatalogCultures, Error> From(CatalogOptions options) =>
        from defaultCulture in CultureCode.Parse(options.DefaultCulture)
        from all in options.Cultures.Traverse(CultureCode.Parse)
        select new CatalogCultures(defaultCulture, all.Prepend(defaultCulture).Distinct().ToEquatableArray());

    /// <summary>The indexed culture to search in: the first requested one that is indexed (exactly or by language).</summary>
    public CultureCode IndexFor(EquatableArray<CultureCode> requested) =>
        requested.SelectMany(c => (CultureCode[])[c, c.Language]).FirstOrDefault(All.Contains) ?? Default;
}
