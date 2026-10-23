using FCommerce.Application.Messaging;
using FCommerce.Settings.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace FCommerce.Host.Security;

/// <summary>Marks a page (model) or endpoint as part of a feature: when the feature is off, it does not exist (404).</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequiresFeatureAttribute(string featureId) : Attribute
{
    public string FeatureId { get; } = featureId;
}

/// <summary>Applied to every Razor Page: answers 404 for pages of switched-off features.</summary>
internal sealed class FeatureGateFilter(IQueryHandler<GetPlatformSettings, PlatformSettingsView> settings) : IAsyncPageFilter
{
    public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context) => Task.CompletedTask;

    public async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        var required = context.ActionDescriptor.EndpointMetadata.OfType<RequiresFeatureAttribute>().Select(a => a.FeatureId).ToList();
        if (required.Count > 0)
        {
            var current = await settings.HandleAsync(new GetPlatformSettings(), context.HttpContext.RequestAborted);
            if (!required.All(current.IsOn))
            {
                context.Result = new NotFoundResult();
                return;
            }
        }

        await next();
    }
}

public static class FeatureGateEndpoints
{
    /// <summary>The minimal API counterpart of <see cref="RequiresFeatureAttribute"/>.</summary>
    public static TBuilder RequireFeature<TBuilder>(this TBuilder builder, string featureId)
        where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (context, next) =>
        {
            var settings = context.HttpContext.RequestServices.GetRequiredService<IQueryHandler<GetPlatformSettings, PlatformSettingsView>>();
            return (await settings.HandleAsync(new GetPlatformSettings(), context.HttpContext.RequestAborted)).IsOn(featureId)
                ? await next(context)
                : Results.NotFound();
        });
}
