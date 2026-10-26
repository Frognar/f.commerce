using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace FCommerce.Host.Api;

/// <summary>The OpenAPI document of the public API (served at /openapi/public-v1.json).</summary>
internal static class PublicApiDocument
{
    public static IServiceCollection AddPublicApiDocument(this IServiceCollection services) =>
        services.AddOpenApi(PublicApiEndpoints.OpenApiDocument, options =>
        {
            // Only the public API: endpoints without a group name (the integration API) belong to the "v1" document.
            options.ShouldInclude = description => description.GroupName == PublicApiEndpoints.OpenApiDocument;
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info = new OpenApiInfo
                {
                    Title = "f.commerce public API",
                    Version = "v1",
                    Description = "The storefront's functions for the systems of a customer: catalog with the customer's prices and stock, cart, orders and documents. "
                        + "A key acts as the user it was issued to, for one customer, with that user's rights. Create keys in the customer area (API keys).",
                };
                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
                document.Components.SecuritySchemes["ApiKey"] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.ApiKey,
                    In = ParameterLocation.Header,
                    Name = ApiKeyAuthenticationHandler.HeaderName,
                    Description = "The API key (fck_…).",
                };
                document.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("ApiKey", document)] = [] }];
                return Task.CompletedTask;
            });
        });
}
