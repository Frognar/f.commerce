using System.Collections.Immutable;
using System.Text.Json;
using FCommerce.Application.Messaging;
using FCommerce.Application.Results;
using FCommerce.Infrastructure;
using FCommerce.Infrastructure.Outbox;
using FCommerce.Infrastructure.Persistence;
using FCommerce.Settings.Contracts;
using FCommerce.Settings.Domain;
using FCommerce.Settings.Persistence;
using FCommerce.SharedKernel.Features;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FCommerce.Settings;

/// <summary>What this installation offers; set by the vendor at deployment, not editable in the back-office.</summary>
public sealed record SettingsOptions(ImmutableHashSet<FeatureId> AvailableFeatures);

public static class SettingsModule
{
    public static IServiceCollection AddSettingsModule(this IServiceCollection services, SettingsOptions options)
    {
        services.AddSingleton(options);
        services.AddSingleton<SettingsSnapshot>();
        services.AddHostedService<SettingsLoader>();
        services.AddModuleDbContext<SettingsDbContext>(SettingsDbContext.Schema);
        services.AddOutbox<SettingsDbContext>();
        services.AddMessageType<PlatformSettingsChanged>("settings.platform-settings-changed.v1");
        services.AddScoped<IQueryHandler<GetPlatformSettings, PlatformSettingsView>, GetPlatformSettingsHandler>();
        services.AddScoped<ICommandHandler<UpdatePlatformSettings, Result<long, Error>>, UpdatePlatformSettingsHandler>();
        services.AddScoped<IQueryHandler<GetSettingsHistory, IReadOnlyList<SettingsChange>>, GetSettingsHistoryHandler>();
        return services;
    }
}

/// <summary>The current settings in memory: read on every request, replaced after each save.</summary>
internal sealed class SettingsSnapshot(SettingsOptions options)
{
    private volatile PlatformSettings current = PlatformSettings.Defaults(options.AvailableFeatures);

    public PlatformSettings Current => current;

    public ImmutableHashSet<FeatureId> Available => options.AvailableFeatures;

    public void Replace(PlatformSettings settings) => current = settings;
}

/// <summary>Loads the settings before the host serves requests; a new installation stores the defaults.</summary>
internal sealed class SettingsLoader(IServiceScopeFactory scopes, SettingsSnapshot snapshot, TimeProvider time) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SettingsDbContext>();
        var row = await db.Settings.AsNoTracking().SingleOrNoneAsync(s => s.Id == SettingsRow.SingletonId, cancellationToken);
        snapshot.Replace(await row.Match(
            stored => Task.FromResult(SettingsDocument.Read(stored.Document, stored.Version, snapshot.Available)),
            async () =>
            {
                var defaults = PlatformSettings.Defaults(snapshot.Available);
                db.Settings.Add(new SettingsRow { Version = defaults.Version, Document = SettingsDocument.Write(defaults), UpdatedAt = time.GetUtcNow() });
                await db.SaveChangesAsync(cancellationToken);
                return defaults;
            }));
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class GetPlatformSettingsHandler(SettingsSnapshot snapshot) : IQueryHandler<GetPlatformSettings, PlatformSettingsView>
{
    public Task<PlatformSettingsView> HandleAsync(GetPlatformSettings query, CancellationToken cancellationToken = default) =>
        Task.FromResult(View(snapshot.Current, snapshot.Available));

    internal static PlatformSettingsView View(PlatformSettings settings, ImmutableHashSet<FeatureId> available)
    {
        var features = settings.Features(available);
        return new PlatformSettingsView(
            settings.Version,
            settings.CatalogVisibility.ToString(),
            [.. Feature.All.Where(f => available.Contains(f.Id)).Select(f => new FeatureView(
                f.Id.Value, f.Description, settings.EnabledFeatures.Contains(f.Id), features.IsOn(f.Id), [.. f.RequiresAll.Select(r => r.Value)]))],
            new BrandingView(settings.Branding.StoreName.Value, settings.Branding.AccentColor.Value, settings.Branding.Logo.Map(l => l.Id).OrNull()),
            new ContactView(
                settings.Contact.SupportEmail.Map(e => e.Value).OrNull(),
                settings.Contact.SupportPhone.Map(p => p.Value).OrNull(),
                settings.Contact.OperatorNotifications.Map(e => e.Value).OrNull()),
            settings.Commerce.DefaultPriceList.Map(p => p.Value).OrNull(),
            settings.Commerce.DefaultContactTemplate.Value,
            settings.Stock.Mode switch
            {
                StockDisplayMode.Levels => StockDisplayModes.Levels,
                StockDisplayMode.Exact => StockDisplayModes.Exact,
                _ => throw new ArgumentOutOfRangeException(nameof(settings), settings.Stock.Mode, "A stock display without a name."),
            },
            settings.Stock.LowThreshold.Value);
    }
}

internal sealed class UpdatePlatformSettingsHandler(SettingsDbContext db, Outbox<SettingsDbContext> outbox, SettingsSnapshot snapshot, TimeProvider time)
    : ICommandHandler<UpdatePlatformSettings, Result<long, Error>>
{
    public async Task<Result<long, Error>> HandleAsync(UpdatePlatformSettings command, CancellationToken cancellationToken = default)
    {
        var row = await db.Settings.SingleAsync(s => s.Id == SettingsRow.SingletonId, cancellationToken);
        var before = SettingsDocument.Read(row.Document, row.Version, snapshot.Available);
        var updated = SettingsRules.Update(
            before,
            command.Version,
            new UnvalidatedSettings(
                command.CatalogVisibility.OrEmpty(),
                command.EnabledFeatures.OrEmpty(),
                command.StoreName.OrEmpty(),
                command.AccentColor.OrEmpty(),
                command.LogoImageId.ToMaybe(),
                command.SupportEmail.ToMaybe(),
                command.SupportPhone.ToMaybe(),
                command.OperatorNotifications.ToMaybe(),
                command.DefaultPriceListCode.ToMaybe(),
                command.DefaultContactTemplate.OrEmpty(),
                command.StockDisplay.OrEmpty(),
                command.LowStockThreshold),
            snapshot.Available);

        return await updated
            .Bind(after => outbox.Enqueue(new PlatformSettingsChanged(after.Version)).Map(_ => after))
            .BindAsync(async after =>
            {
                var now = time.GetUtcNow();
                row.Version = after.Version;
                row.Document = SettingsDocument.Write(after);
                row.UpdatedAt = now;
                row.UpdatedBy = command.ActorId;
                db.History.Add(new SettingsHistoryRow
                {
                    Version = after.Version,
                    At = now,
                    ActorId = command.ActorId,
                    Changes = JsonSerializer.Serialize(SettingsRules.Changes(before, after)),
                    Document = row.Document,
                });

                try
                {
                    await db.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateConcurrencyException)
                {
                    return SettingsRules.ChangedMeanwhile;
                }

                snapshot.Replace(after);
                return Result.Ok<long, Error>(after.Version);
            });
    }
}

internal sealed class GetSettingsHistoryHandler(SettingsDbContext db) : IQueryHandler<GetSettingsHistory, IReadOnlyList<SettingsChange>>
{
    public async Task<IReadOnlyList<SettingsChange>> HandleAsync(GetSettingsHistory query, CancellationToken cancellationToken = default) =>
        [.. (await db.History.AsNoTracking().OrderByDescending(h => h.Version).Take(Math.Clamp(query.Take, 1, 200)).ToListAsync(cancellationToken))
            .Select(h => new SettingsChange(h.Version, h.At, h.ActorId, JsonSerializer.Deserialize<string[]>(h.Changes) ?? []))];
}
