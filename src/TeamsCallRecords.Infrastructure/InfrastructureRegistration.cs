using Azure.Core;
using Azure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TeamsCallRecords.Infrastructure.Config;
using TeamsCallRecords.Infrastructure.Data;
using TeamsCallRecords.Infrastructure.Diagnostics;
using TeamsCallRecords.Infrastructure.Graph;
using TeamsCallRecords.Infrastructure.Outbox;
using TeamsCallRecords.Infrastructure.Processing;
using TeamsCallRecords.Infrastructure.Queue;
using TeamsCallRecords.Infrastructure.Subscriptions;

namespace TeamsCallRecords.Infrastructure;

/// <summary>DI registration, settings validation and startup migration (TRP 3.18).</summary>
public static class InfrastructureRegistration
{
    /// <summary>
    /// Registers storage, queues, processing and outbox. IGraphClient is registered by the host
    /// (a real client or the in-memory demo client).
    /// </summary>
    public static IServiceCollection AddTcriInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);

        AddSection<StorageOptions>(services, configuration, "Storage", o => o.Validate());
        AddSection<InboxOptions>(services, configuration, "Inbox", o => o.Validate());
        AddSection<ProcessingOptions>(services, configuration, "Processing", o => o.Validate());
        AddSection<ReconcileOptions>(services, configuration, "Reconcile", o => o.Validate());
        AddSection<OutboxOptions>(services, configuration, "Outbox", o => o.Validate());
        AddSection<GraphOptions>(services, configuration, "Graph", o => o.Validate());
        AddSection<SubscriptionOptions>(services, configuration, "Subscriptions", o => o.Validate());

        services.AddSingleton<SqlitePragmaInterceptor>();
        services.AddDbContextFactory<TcriDbContext>((sp, options) =>
        {
            var storage = sp.GetRequiredService<IOptions<StorageOptions>>().Value;
            var path = Path.GetFullPath(Path.Combine(storage.DataDirectory, storage.DatabaseFileName));
            options.UseSqlite($"Data Source={path};Mode=ReadWriteCreate;Pooling=True")
                   .AddInterceptors(sp.GetRequiredService<SqlitePragmaInterceptor>());
        });
        services.AddSingleton<IWriteGate, WriteGate>();

        services.AddSingleton<IInboxStore, InboxStore>();
        services.AddSingleton<IOutboxStore, OutboxStore>();
        services.AddSingleton<ISourceStateStore, SourceStateStore>();
        services.AddSingleton<ISubscriptionStore, SubscriptionStore>();
        services.AddSingleton<IErrorLog, ErrorLog>();
        services.AddSingleton<ICdrReader, CdrReader>();

        services.AddSingleton<ICdrApplier, CdrApplier>();
        services.AddSingleton<ICallProcessor, CallProcessor>();
        services.AddSingleton<IReconciler, Reconciler>();
        services.AddSingleton<IOutboxSender, OutboxSender>();
        return services;
    }

    /// <summary>
    /// Registers the real Microsoft Graph client (TRP 3.14, 3.18): typed IGraphClient, token from
    /// ClientSecretCredential, resilience pipeline. Requires AddTcriInfrastructure; adds the live-only options rules.
    /// </summary>
    public static IHttpClientBuilder AddTcriGraphClient(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IValidateOptions<GraphOptions>>(new DelegateOptionsValidator<GraphOptions>(o => o.ValidateLive()));
        services.TryAddSingleton<TokenCredential>(sp =>
        {
            var o = sp.GetRequiredService<IOptions<GraphOptions>>().Value;
            return new ClientSecretCredential(o.TenantId, o.ClientId, o.ClientSecret);
        });
        services.AddSingleton<GraphResiliencePipeline>();
        services.AddTransient<GraphResilienceHandler>();

        return services.AddHttpClient<IGraphClient, GraphClient>()
            .ConfigureHttpClient(c => c.Timeout = Timeout.InfiniteTimeSpan)   // timeouts are owned by the resilience pipeline
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2) })
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan)   // IGraphClient is used by singletons; connection lifetime above handles DNS changes
            .AddHttpMessageHandler<GraphResilienceHandler>();
    }

    private static void AddSection<T>(IServiceCollection services, IConfiguration configuration, string name,
        Func<T, IEnumerable<string>> validate) where T : class
    {
        services.AddOptions<T>().Bind(configuration.GetSection(name)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<T>>(new DelegateOptionsValidator<T>(validate));
    }

    /// <summary>
    /// Creates the data directory, applies migrations and checks that the journal is in WAL mode.
    /// Throws InvalidOperationException otherwise.
    /// </summary>
    public static async Task MigrateAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        var storage = services.GetRequiredService<IOptions<StorageOptions>>().Value;
        Directory.CreateDirectory(Path.GetFullPath(storage.DataDirectory));

        var factory = services.GetRequiredService<IDbContextFactory<TcriDbContext>>();
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Database.MigrateAsync(ct);

        await db.Database.OpenConnectionAsync(ct);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        var mode = Convert.ToString(await command.ExecuteScalarAsync(ct));
        if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"SQLite journal_mode is '{mode}', expected 'wal'");

        var applied = (await db.Database.GetAppliedMigrationsAsync(ct)).LastOrDefault();
        services.GetService<ILoggerFactory>()?.CreateLogger("TeamsCallRecords.Startup")
            .LogInformation("Database ready, last applied migration: {Migration}", applied);
    }
}
