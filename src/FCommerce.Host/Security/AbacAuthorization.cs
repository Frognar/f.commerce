using FCommerce.Authorization.Core;
using Microsoft.AspNetCore.Authorization;

namespace FCommerce.Host.Security;

/// <summary>Requires the ABAC decision for an action on a (request-independent) resource to be "permit".</summary>
public sealed record AbacRequirement(AccessAction Action, Resource Resource) : IAuthorizationRequirement;

/// <summary>Policy enforcement point: ASP.NET Core authorization delegates every decision to the platform policies.</summary>
internal sealed partial class AbacAuthorizationHandler(AccessControl access, ILogger<AbacAuthorizationHandler> logger) : AuthorizationHandler<AbacRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, AbacRequirement requirement)
    {
        var decision = await access.DecideAsync(requirement.Action, requirement.Resource);
        if (decision is Decision.Denied denied)
        {
            LogDenied(requirement.Action, requirement.Resource, denied);
            // Not succeeding lets ASP.NET Core challenge anonymous users (sign-in) and forbid signed-in ones.
            return;
        }

        context.Succeed(requirement);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Access denied: {Action} on {Resource}: {Decision}")]
    private partial void LogDenied(AccessAction action, Resource resource, Decision.Denied decision);
}

public static class Policies
{
    public const string CatalogRead = "catalog.read";
    public const string OwnAccount = "account.own";
    public const string BackOffice = "back-office";
    public const string OperatorsManage = "operators.manage";

    public static IServiceCollection AddAbacAuthorization(this IServiceCollection services) =>
        services
            .AddScoped<IAuthorizationHandler, AbacAuthorizationHandler>()
            .AddAuthorizationBuilder()
            .AddPolicy(CatalogRead, p => p.AddRequirements(new AbacRequirement(AccessAction.Read, new Resource.Catalog())))
            .AddPolicy(OwnAccount, p => p.AddRequirements(new AbacRequirement(AccessAction.Manage, new Resource.OwnAccount())))
            .AddPolicy(BackOffice, p => p.AddRequirements(new AbacRequirement(AccessAction.Read, new Resource.Customers())))
            .AddPolicy(OperatorsManage, p => p.AddRequirements(new AbacRequirement(AccessAction.Manage, new Resource.Operators())))
            .Services;
}
