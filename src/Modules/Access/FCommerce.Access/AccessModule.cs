using FCommerce.Access.Contracts;
using FCommerce.Access.Handlers;
using FCommerce.Access.Persistence;
using FCommerce.Application.Messaging;
using FCommerce.Authorization.Core;
using FCommerce.Infrastructure;
using FCommerce.Infrastructure.Outbox;
using FCommerce.Infrastructure.Persistence;
using FCommerce.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SignInCommand = FCommerce.Access.Contracts.SignIn;

namespace FCommerce.Access;

public sealed class AccessOptions
{
    /// <summary>When set and no operator exists yet, this person is invited as platform administrator on startup.</summary>
    public string? BootstrapAdminEmail { get; set; }

    public string BootstrapAdminName { get; set; } = "Administrator";

    public string BootstrapCulture { get; set; } = "pl";
}

public static class AccessModule
{
    public static IServiceCollection AddAccessModule(this IServiceCollection services, AccessOptions options)
    {
        services.AddSingleton(options);
        services.AddModuleDbContext<AccessDbContext>(AccessDbContext.Schema);
        services.AddOutbox<AccessDbContext>();
        services.AddMessageType<InvitationIssued>("access.invitation-issued.v1");
        services.AddMessageType<PasswordResetRequested>("access.password-reset-requested.v1");

        services.AddScoped<AccessStore>();
        services.AddScoped<OperatorManagement>();
        services.AddScoped<ICommandHandler<SignInCommand, Result<SignedIn, Error>>, SignInHandler>();
        services.AddScoped<IQueryHandler<GetSession, Result<SessionView, Error>>, GetSessionHandler>();
        services.AddScoped<IQueryHandler<GetInvitation, Result<InvitationView, Error>>, GetInvitationHandler>();
        services.AddScoped<ICommandHandler<AcceptInvitation, Result<SignedIn, Error>>, AcceptInvitationHandler>();
        services.AddScoped<ICommandHandler<RequestPasswordReset, Result<Unit, Error>>, RequestPasswordResetHandler>();
        services.AddScoped<ICommandHandler<ResetPassword, Result<Unit, Error>>, ResetPasswordHandler>();
        services.AddScoped<ICommandHandler<ChangePassword, Result<SignedIn, Error>>, ChangePasswordHandler>();
        services.AddScoped<ICommandHandler<ChangeProfile, Result<Unit, Error>>, ChangeProfileHandler>();
        services.AddScoped<ICommandHandler<InviteOperator, Result<Guid, Error>>, InviteOperatorHandler>();
        services.AddScoped<ICommandHandler<ChangeOperator, Result<Unit, Error>>, ChangeOperatorHandler>();
        services.AddScoped<ICommandHandler<RevokeOperator, Result<Unit, Error>>, RevokeOperatorHandler>();
        services.AddScoped<IQueryHandler<GetOperators, IReadOnlyList<OperatorSummary>>, GetOperatorsHandler>();
        services.AddHostedService<OperatorBootstrap>();
        return services;
    }
}

/// <summary>Invites the first platform administrator when the installation has no operator yet.</summary>
internal sealed partial class OperatorBootstrap(IServiceScopeFactory scopes, AccessOptions options, IHostApplicationLifetime lifetime, ILogger<OperatorBootstrap> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(options.BootstrapAdminEmail))
        {
            return;
        }

        var started = new TaskCompletionSource();
        await using (lifetime.ApplicationStarted.Register(() => started.TrySetResult()))
        await using (stoppingToken.Register(() => started.TrySetCanceled(stoppingToken)))
        {
            try
            {
                await started.Task;
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AccessDbContext>();
        if (await db.Accounts.AnyAsync(a => a.Operator != null, stoppingToken))
        {
            return;
        }

        var invited = await scope.ServiceProvider.GetRequiredService<OperatorManagement>().InviteAsync(
            actor: null,
            options.BootstrapAdminEmail,
            options.BootstrapAdminName,
            options.BootstrapCulture,
            AccessStore.ToView(OperatorAttributes.PlatformAdministrator),
            stoppingToken);
        invited.Switch(_ => LogInvited(options.BootstrapAdminEmail), error => LogFailed(error.Message));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "No operator yet: invited {Email} as platform administrator")]
    private partial void LogInvited(string email);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not invite the first platform administrator: {Reason}")]
    private partial void LogFailed(string reason);
}
