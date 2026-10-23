using FCommerce.Application.Messaging;
using FCommerce.Settings.Contracts;

namespace FCommerce.Host.Security;

/// <summary>Marks a page (model) or endpoint as part of a feature: when the feature is off, it does not exist (404).</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequiresFeatureAttribute(string featureId) : Attribute
{
    public string FeatureId { get; } = featureId;
}

public static class FeatureGate
{
    /// <summary>
    /// Answers 404 for endpoints of switched-off features. Runs right after routing, before authentication and
    /// authorization, so a switched-off feature looks the same to everybody (no sign-in or access-denied redirect).
    /// </summary>
    public static IApplicationBuilder UseFeatureGate(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var required = context.GetEndpoint()?.Metadata.GetOrderedMetadata<RequiresFeatureAttribute>() ?? [];
            if (required.Count > 0)
            {
                var settings = context.RequestServices.GetRequiredService<IQueryHandler<GetPlatformSettings, PlatformSettingsView>>();
                var current = await settings.HandleAsync(new GetPlatformSettings(), context.RequestAborted);
                if (!required.All(r => current.IsOn(r.FeatureId)))
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }
            }

            await next(context);
        });

    /// <summary>The minimal API counterpart of <see cref="RequiresFeatureAttribute"/>.</summary>
    public static TBuilder RequireFeature<TBuilder>(this TBuilder builder, string featureId)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new RequiresFeatureAttribute(featureId));
}
