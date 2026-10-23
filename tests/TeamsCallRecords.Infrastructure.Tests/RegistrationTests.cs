using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TeamsCallRecords.Infrastructure.Config;
using TeamsCallRecords.Infrastructure.Data;
using TeamsCallRecords.Infrastructure.Graph;
using TeamsCallRecords.Infrastructure.Outbox;
using TeamsCallRecords.Infrastructure.Processing;
using TeamsCallRecords.Infrastructure.Queue;
using TeamsCallRecords.Infrastructure.Tests.Support;

namespace TeamsCallRecords.Infrastructure.Tests;

public class RegistrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tcri-reg", Guid.NewGuid().ToString("N"));

    private ServiceProvider Build(Dictionary<string, string?>? extra = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["Storage:DataDirectory"] = _dir,
            ["Graph:TenantId"] = TestDb.Tenant,
        };
        if (extra != null) foreach (var kv in extra) values[kv.Key] = kv.Value;
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IGraphClient, FakeGraphClient>();
        services.AddTcriInfrastructure(config);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void AllServicesResolve()
    {
        using var sp = Build();
        Assert.NotNull(sp.GetRequiredService<IInboxStore>());
        Assert.NotNull(sp.GetRequiredService<IOutboxStore>());
        Assert.NotNull(sp.GetRequiredService<ISourceStateStore>());
        Assert.NotNull(sp.GetRequiredService<ICdrReader>());
        Assert.NotNull(sp.GetRequiredService<ICdrApplier>());
        Assert.NotNull(sp.GetRequiredService<ICallProcessor>());
        Assert.NotNull(sp.GetRequiredService<IReconciler>());
        Assert.NotNull(sp.GetRequiredService<IOutboxSender>());
    }

    [Fact]
    public void Services_AreSingletons()
    {
        using var sp = Build();
        Assert.Same(sp.GetRequiredService<IWriteGate>(), sp.GetRequiredService<IWriteGate>());
        Assert.Same(sp.GetRequiredService<ICallProcessor>(), sp.GetRequiredService<ICallProcessor>());
    }

    [Fact]
    public async Task MigrateAsync_CreatesDirectoryAndWalDatabase()
    {
        using var sp = Build();
        await sp.MigrateAsync();
        Assert.True(File.Exists(Path.Combine(_dir, "tcri.db")));

        var stats = await sp.GetRequiredService<IInboxStore>().GetStatsAsync(default);
        Assert.Equal(new InboxStats(0, 0, 0), stats);
    }

    [Fact]
    public async Task MigrateAsync_Twice_IsHarmless()
    {
        using var sp = Build();
        await sp.MigrateAsync();
        await sp.MigrateAsync();
    }

    [Theory]
    [InlineData("Inbox:MaxPending", "0")]
    [InlineData("Processing:Workers", "99")]
    [InlineData("Processing:RetrySchedule", "1,x")]
    [InlineData("Reconcile:IntervalMinutes", "0")]
    [InlineData("Outbox:MaxAttempts", "0")]
    [InlineData("Storage:DatabaseFileName", "a/b.db")]
    [InlineData("Graph:RetentionDays", "0")]
    public void InvalidSettings_AreRejected(string key, string value)
    {
        using var sp = Build(new Dictionary<string, string?> { [key] = value });
        Assert.Throws<OptionsValidationException>(() => Touch(sp));
    }

    private static object? Touch(IServiceProvider sp)
    {
        // access every options object: the invalid one throws
        sp.GetRequiredService<IOptions<StorageOptions>>().Value.ToString();
        sp.GetRequiredService<IOptions<InboxOptions>>().Value.ToString();
        sp.GetRequiredService<IOptions<ProcessingOptions>>().Value.ToString();
        sp.GetRequiredService<IOptions<ReconcileOptions>>().Value.ToString();
        sp.GetRequiredService<IOptions<OutboxOptions>>().Value.ToString();
        sp.GetRequiredService<IOptions<GraphOptions>>().Value.ToString();
        return null;
    }

    [Fact]
    public void MissingTenant_IsRejected()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:DataDirectory"] = _dir,
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTcriInfrastructure(config);
        using var sp = services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => sp.GetRequiredService<IOptions<GraphOptions>>().Value);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }
}
