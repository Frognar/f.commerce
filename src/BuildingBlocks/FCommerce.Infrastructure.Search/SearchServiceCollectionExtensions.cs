using FCommerce.Application.Search;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FCommerce.Infrastructure.Search;

public static class SearchServiceCollectionExtensions
{
    /// <summary>The embedded search engine, storing its indexes under <paramref name="rootPath"/>.</summary>
    public static IServiceCollection AddEmbeddedSearch(this IServiceCollection services, string rootPath) =>
        services.AddSingleton<ISearchEngine>(sp => new EmbeddedSearchEngine(rootPath, sp.GetRequiredService<ILoggerFactory>()));
}
