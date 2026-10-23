using FCommerce.Application.EventStore;
using FCommerce.Infrastructure.Persistence;
using FCommerce.Infrastructure.Postgres.EventStore;
using FCommerce.Infrastructure.Postgres.Persistence;
using FCommerce.Infrastructure.Serialization;
using JasperFx;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Weasel.Core;
using IDatabaseProvider = FCommerce.Infrastructure.Persistence.IDatabaseProvider;

namespace FCommerce.Infrastructure.Postgres;

public static class PostgresServiceCollectionExtensions
{
    public const string EventsSchema = "events";

    /// <summary>
    /// PostgreSQL adapter: Marten-backed <see cref="IEventStore"/> and the Npgsql EF Core provider for every
    /// module DbContext. <paramref name="applySchemaChanges"/> creates/updates Marten's event tables
    /// on startup (development and tests); EF Core tables are managed by migrations.
    /// </summary>
    public static IServiceCollection AddPostgres(this IServiceCollection services, string connectionString, bool applySchemaChanges)
    {
        var marten = services.AddMarten(sp =>
        {
            var options = new StoreOptions();
            options.Connection(connectionString);
            options.DatabaseSchemaName = EventsSchema;
            options.Events.DatabaseSchemaName = EventsSchema;
            options.Events.StreamIdentity = JasperFx.Events.StreamIdentity.AsString;
            options.Events.MetadataConfig.CorrelationIdEnabled = true;
            options.Events.MetadataConfig.CausationIdEnabled = true;
            options.Events.MetadataConfig.HeadersEnabled = true;
            options.UseSystemTextJsonForSerialization(
                PersistenceJson.Configure(new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)),
                EnumStorage.AsString,
                Casing.CamelCase);
            options.AutoCreateSchemaObjects = applySchemaChanges ? AutoCreate.CreateOrUpdate : AutoCreate.None;

            foreach (var registration in sp.GetRequiredService<MessageTypeRegistry>().All)
            {
                options.Events.MapEventType(registration.Type, registration.Name);
            }

            return options;
        }).UseLightweightSessions();

        if (applySchemaChanges)
        {
            marten.ApplyAllDatabaseChangesOnStartup();
        }

        services.AddSingleton<IEventStore, MartenEventStore>();
        services.AddSingleton<IDatabaseProvider>(new PostgresDatabaseProvider(connectionString));

        return services;
    }
}
