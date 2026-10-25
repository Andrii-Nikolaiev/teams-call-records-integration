using System.Collections.Concurrent;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using TeamsCallRecords.Host.Web;
using TeamsCallRecords.Infrastructure.Data.Entities;
using TeamsCallRecords.Infrastructure.Queue;
using TeamsCallRecords.Infrastructure.Subscriptions;

namespace TeamsCallRecords.Host.Tests;

/// <summary>Records what the webhook commits to the inbox; nothing is processed. Fails on demand.</summary>
public sealed class RecordingInbox : IInboxStore
{
    public ConcurrentQueue<InboxNewItem> Items { get; } = new();
    public volatile bool Fail;

    public Task<InboxEnqueueResult> EnqueueAsync(IReadOnlyList<InboxNewItem> items, CancellationToken ct)
    {
        if (Fail) throw new InvalidOperationException("database unavailable");
        foreach (var item in items) Items.Enqueue(item);
        return Task.FromResult(new InboxEnqueueResult(items.Count, 0, false));
    }

    public Task<InboxItem?> ClaimNextAsync(CancellationToken ct) => Task.FromResult<InboxItem?>(null);
    public Task<DateTimeOffset?> RenewLeaseAsync(long id, DateTimeOffset expectedLockedUntilUtc, CancellationToken ct) => Task.FromResult<DateTimeOffset?>(null);
    public Task CompleteAsync(long id, CancellationToken ct) => Task.CompletedTask;
    public Task RescheduleAsync(long id, DateTimeOffset nextAttemptUtc, string? error, bool resetAttempts, CancellationToken ct) => Task.CompletedTask;
    public Task FailAsync(long id, string error, CancellationToken ct) => Task.CompletedTask;
    public Task<InboxStats> GetStatsAsync(CancellationToken ct) => Task.FromResult(new InboxStats(0, 0, 0));
}

/// <summary>Records lifecycle calls made by the webhook.</summary>
public sealed class RecordingSubscriptionService : ISubscriptionService
{
    public ConcurrentQueue<string> Calls { get; } = new();
    public volatile bool Fail;

    public Task<bool> EnsureAsync(CancellationToken ct) => Task.FromResult(true);

    public Task HandleLifecycleAsync(string subscriptionId, string lifecycleEvent, CancellationToken ct)
    {
        if (Fail) throw new InvalidOperationException("database unavailable");
        Calls.Enqueue(subscriptionId + ":" + lifecycleEvent);
        return Task.CompletedTask;
    }
}

/// <summary>
/// The real Host with managed subscriptions (Graph:NotificationUrl is set), a real SQLite subscription table,
/// and recording stand-ins for the inbox and the subscription service.
/// </summary>
public sealed class ManagedWebhookFactory : WebApplicationFactory<Program>
{
    public const string Tenant = TcriFactory.Tenant;
    public const string State = "managed-client-state-01";
    public const string Resource = "communications/callRecords";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tcri-host-managed-" + Guid.NewGuid().ToString("N"));

    public RecordingInbox Inbox { get; } = new();
    public RecordingSubscriptionService Subscriptions { get; } = new();

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.UseSetting("Storage:DataDirectory", _dir);
        builder.UseSetting("Api:Key", TcriFactory.ApiKey);
        builder.UseSetting("Webhook:ClientState", TcriFactory.ClientState);
        builder.UseSetting("Webhook:SubscriptionCreationGraceSeconds", "2");
        builder.UseSetting("Demo:Enabled", "true");
        builder.UseSetting("Graph:TenantId", Tenant);
        builder.UseSetting("Graph:NotificationUrl", "https://tcri.example.test/graph/notify");
        builder.UseSetting("Graph:ClientState", State);
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IInboxStore>(Inbox);
            services.AddSingleton<ISubscriptionService>(Subscriptions);
        });
    }

    public PendingSubscriptionRegistry Pending => Services.GetRequiredService<PendingSubscriptionRegistry>();

    /// <summary>Stores a subscription row the way SubscriptionService would.</summary>
    public Task Seed(string id, string status = SubscriptionStatuses.Active, string state = State, string resource = Resource) =>
        Services.GetRequiredService<ISubscriptionStore>().UpsertAsync(new SubscriptionRow
        {
            Id = id,
            Kind = SubscriptionKinds.CallRecords,
            Resource = resource,
            NotificationUrl = "https://tcri.example.test/graph/notify",
            ExpiresUtc = "2099-01-01T00:00:00.0000000Z",
            ClientStateHash = SubscriptionStore.HashClientState(state),
            Status = status,
            CreatedUtc = "2026-10-25T12:00:00.0000000Z",
        }, default);

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }
        catch (IOException) { /* temp files; the OS cleans them later */ }
        catch (UnauthorizedAccessException) { }
    }
}
