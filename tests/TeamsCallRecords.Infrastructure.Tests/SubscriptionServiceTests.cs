using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TeamsCallRecords.Infrastructure.Data;
using TeamsCallRecords.Infrastructure.Data.Entities;
using TeamsCallRecords.Infrastructure.Graph;
using TeamsCallRecords.Infrastructure.Queue;
using TeamsCallRecords.Infrastructure.Subscriptions;
using TeamsCallRecords.Infrastructure.Tests.Support;

namespace TeamsCallRecords.Infrastructure.Tests;

public class SubscriptionServiceTests : IDisposable
{
    private const string Url = "https://tcri.example.test/graph/notify";
    private const string State = "secret-client-state-1";
    private const string CallRecords = "communications/callRecords";
    private static readonly TimeSpan Full = TimeSpan.FromMinutes(4230);

    private sealed class RecordingMarker : IPendingSubscriptionMarker
    {
        public List<(string Id, string Tenant, string Resource, string Hash)> Marked { get; } = new();
        public List<string> Cleared { get; } = new();
        public void Mark(string subscriptionId, string tenantId, string resource, string clientStateHash) =>
            Marked.Add((subscriptionId, tenantId, resource, clientStateHash));
        public void Clear(string subscriptionId) => Cleared.Add(subscriptionId);
    }

    private readonly Env _env = new();
    private readonly SubscriptionStore _store;
    private readonly SubscriptionOptions _options = new();
    private readonly RecordingMarker _marker = new();
    private SubscriptionService _service = null!;

    public SubscriptionServiceTests()
    {
        _env.Graph.NotificationUrl = Url;
        _env.Graph.ClientState = State;
        _store = new SubscriptionStore(_env.Db.Gate, _env.Db.Factory);
        _service = Build();
    }

    public void Dispose() => _env.Dispose();

    private SubscriptionService Build() => new(_env.FakeGraph, _store, _env.Inbox, _env.Errors, Options.Create(_env.Graph),
        Options.Create(_options), _marker, _env.Db.Clock, NullLogger<SubscriptionService>.Instance);

    private FakeGraphClient Fake => _env.FakeGraph;
    private DateTimeOffset Now => _env.Now;

    private Task Seed(string id, string kind = SubscriptionKinds.CallRecords, TimeSpan? expiresIn = null, string status = SubscriptionStatuses.Active,
        string? url = null, string resource = CallRecords, DateTimeOffset? created = null) =>
        _store.UpsertAsync(new SubscriptionRow
        {
            Id = id,
            Kind = kind,
            Resource = resource,
            NotificationUrl = url ?? Url,
            ExpiresUtc = UtcText.Format(Now + (expiresIn ?? Full)),
            ClientStateHash = SubscriptionStore.HashClientState(State),
            Status = status,
            CreatedUtc = UtcText.Format(created ?? Now),
        }, default);

    private void SeedGraph(string id, TimeSpan expiresIn, string? url = null, string resource = CallRecords) =>
        Fake.Subscriptions.Add(new GraphSubscriptionInfo(id, resource, url ?? Url, Now + expiresIn));

    private async Task<List<string>> ErrorMessages() => (await _env.ErrorRows()).Select(r => r.Message).ToList();

    private static GraphException Fail(int? status = 500, bool unknown = false) =>
        new("graph failure", status, isTransient: false, inner: null, outcomeUnknown: unknown);

    // ---- Ensure: creation ---------------------------------------------------------------------------------

    [Fact]
    public async Task Ensure_NoRows_CreatesCallRecordsSubscription()
    {
        var ok = await _service.EnsureAsync(default);

        Assert.True(ok);
        Assert.Equal(new[] { "list", "create:" + CallRecords }, Fake.SubscriptionCalls);
        var request = Assert.Single(Fake.CreateRequests);
        Assert.Equal(SubscriptionKinds.CallRecords, request.Kind);
        Assert.Equal(Url, request.NotificationUrl);
        Assert.Equal(State, request.ClientState);
        Assert.Equal(Now + Full, request.ExpiresUtc);

        var row = Assert.Single(await _store.ListAsync(default));
        Assert.Equal("graph-sub-1", row.Id);
        Assert.Equal(SubscriptionStatuses.Active, row.Status);
        Assert.Equal(CallRecords, row.Resource);
        Assert.Equal(Url, row.NotificationUrl);
        Assert.Equal(SubscriptionStore.HashClientState(State), row.ClientStateHash);
        Assert.Equal(UtcText.Format(Now + Full), row.ExpiresUtc);
        Assert.Equal(UtcText.Format(Now), row.CreatedUtc);
        Assert.Null(row.RenewedUtc);
    }

    [Fact]
    public async Task Ensure_NewSubscription_IsMarkedPendingThenCleared()
    {
        await _service.EnsureAsync(default);

        var marked = Assert.Single(_marker.Marked);
        Assert.Equal("graph-sub-1", marked.Id);
        Assert.Equal(TestDb.Tenant, marked.Tenant);
        Assert.Equal(CallRecords, marked.Resource);
        Assert.Equal(SubscriptionStore.HashClientState(State), marked.Hash);
        Assert.Equal(new[] { "graph-sub-1" }, _marker.Cleared);
    }

    [Fact]
    public async Task Ensure_CreateOutcomeUnknown_NextEnsureAdoptsExisting()
    {
        Fake.CreateFault = Fail(null, unknown: true);
        Fake.CreateFaultAfterApply = true;

        var first = await _service.EnsureAsync(default);

        Assert.False(first);
        Assert.Contains("subscription create outcome unknown", await ErrorMessages());
        Assert.Empty(await _store.ListAsync(default));
        Assert.Empty(_marker.Marked);

        Fake.CreateFault = null;
        Fake.SubscriptionCalls.Clear();
        var second = await _service.EnsureAsync(default);

        Assert.True(second);
        Assert.Equal(new[] { "list" }, Fake.SubscriptionCalls);
        var row = Assert.Single(await _store.ListAsync(default));
        Assert.Equal("graph-sub-1", row.Id);
        Assert.Equal(SubscriptionStatuses.Active, row.Status);
    }

    [Fact]
    public async Task Ensure_DuplicatesInGraph_KeepsLatestDeletesOthers()
    {
        SeedGraph("older", TimeSpan.FromHours(10));
        SeedGraph("latest", TimeSpan.FromHours(60));

        var ok = await _service.EnsureAsync(default);

        Assert.True(ok);
        Assert.DoesNotContain(Fake.SubscriptionCalls, c => c.StartsWith("create:"));
        Assert.Contains("delete:older", Fake.SubscriptionCalls);
        Assert.DoesNotContain("delete:latest", Fake.SubscriptionCalls);
        var row = Assert.Single(await _store.ListAsync(default));
        Assert.Equal("latest", row.Id);
        Assert.Equal(UtcText.Format(Now + TimeSpan.FromHours(60)), row.ExpiresUtc);
    }

    [Fact]
    public async Task Ensure_ForeignNotificationUrl_IsNotAdopted()
    {
        SeedGraph("foreign", TimeSpan.FromHours(60), url: "https://someone-else.test/notify");

        var ok = await _service.EnsureAsync(default);

        Assert.True(ok);
        Assert.DoesNotContain("delete:foreign", Fake.SubscriptionCalls);
        Assert.Contains("create:" + CallRecords, Fake.SubscriptionCalls);
        Assert.Contains(Fake.Subscriptions, s => s.Id == "foreign");
        Assert.Equal("graph-sub-1", Assert.Single(await _store.ListAsync(default)).Id);
    }

    [Fact]
    public async Task Ensure_WithTeamAndChannel_CreatesBoth()
    {
        _options.TeamId = "team-1";
        _options.ChannelId = "channel-1";
        _service = Build();

        var ok = await _service.EnsureAsync(default);

        Assert.True(ok);
        Assert.Equal(2, Fake.CreateRequests.Count);
        var channel = Fake.CreateRequests[1];
        Assert.Equal(SubscriptionKinds.ChannelMessages, channel.Kind);
        Assert.Equal("teams/team-1/channels/channel-1/messages", channel.Resource);
        Assert.Equal(Now + TimeSpan.FromMinutes(60), channel.ExpiresUtc);
        Assert.Equal(2, (await _store.ListAsync(default)).Count);
    }

    [Fact]
    public async Task Ensure_TeamAndChannelEmpty_DoesNotTouchChannelRows()
    {
        await Seed("old-channel", kind: SubscriptionKinds.ChannelMessages, expiresIn: TimeSpan.FromMinutes(-5),
            resource: "teams/t/channels/c/messages");
        await Seed("calls", expiresIn: Full);

        var ok = await _service.EnsureAsync(default);

        Assert.True(ok);
        Assert.Empty(Fake.SubscriptionCalls);
        Assert.Equal(2, (await _store.ListAsync(default)).Count);
    }

    // ---- Ensure: renewal ----------------------------------------------------------------------------------

    [Fact]
    public async Task Ensure_ActiveFarFromExpiry_DoesNothing()
    {
        await Seed("sub-1", expiresIn: TimeSpan.FromMinutes(3000));

        var ok = await _service.EnsureAsync(default);

        Assert.True(ok);
        Assert.Empty(Fake.SubscriptionCalls);
    }

    [Fact]
    public async Task Ensure_LessThanHalfLeft_Renews()
    {
        await Seed("sub-1", expiresIn: TimeSpan.FromMinutes(2000));
        SeedGraph("sub-1", TimeSpan.FromMinutes(2000));

        var ok = await _service.EnsureAsync(default);

        Assert.True(ok);
        Assert.Equal(new[] { "renew:sub-1" }, Fake.SubscriptionCalls);
        var row = Assert.Single(await _store.ListAsync(default));
        Assert.Equal(UtcText.Format(Now + Full), row.ExpiresUtc);
        Assert.Equal(UtcText.Format(Now), row.RenewedUtc);
        Assert.Equal(SubscriptionStatuses.Active, row.Status);
    }

    [Fact]
    public async Task Ensure_Renew_KeepsStoredClientStateHash()
    {
        await _store.UpsertAsync(new SubscriptionRow
        {
            Id = "sub-1", Kind = SubscriptionKinds.CallRecords, Resource = CallRecords, NotificationUrl = Url,
            ExpiresUtc = UtcText.Format(Now + TimeSpan.FromMinutes(2000)),
            ClientStateHash = SubscriptionStore.HashClientState("the-state-it-was-created-with"),
            Status = SubscriptionStatuses.Active, CreatedUtc = UtcText.Format(Now),
        }, default);
        SeedGraph("sub-1", TimeSpan.FromMinutes(2000));

        await _service.EnsureAsync(default);

        var row = Assert.Single(await _store.ListAsync(default));
        Assert.Equal(SubscriptionStore.HashClientState("the-state-it-was-created-with"), row.ClientStateHash);
    }

    [Fact]
    public async Task Ensure_RenewFails_CreatesNewAndDeletesOld()
    {
        await Seed("old", expiresIn: TimeSpan.FromMinutes(2000));
        Fake.RenewFault = Fail(404);

        var ok = await _service.EnsureAsync(default);

        Assert.True(ok);
        Assert.Equal(new[] { "renew:old", "list", "create:" + CallRecords, "delete:old" }, Fake.SubscriptionCalls);
        var row = Assert.Single(await _store.ListAsync(default));
        Assert.Equal("graph-sub-1", row.Id);
        Assert.Equal(SubscriptionStatuses.Active, row.Status);
        Assert.Contains("subscription renewal failed", await ErrorMessages());
    }

    [Fact]
    public async Task Ensure_RenewFails_ButSubscriptionStillInGraph_IsAdoptedNotDeleted()
    {
        await Seed("old", expiresIn: TimeSpan.FromMinutes(2000));
        SeedGraph("old", TimeSpan.FromMinutes(2000));
        Fake.RenewFault = Fail(503);

        var ok = await _service.EnsureAsync(default);

        Assert.True(ok);
        Assert.DoesNotContain(Fake.SubscriptionCalls, c => c.StartsWith("create:") || c == "delete:old");
        var row = Assert.Single(await _store.ListAsync(default));
        Assert.Equal("old", row.Id);
        Assert.Equal(SubscriptionStatuses.Active, row.Status);
    }

    [Fact]
    public async Task Ensure_RenewFailsAndCreateFails_OldRowStaysFailed()
    {
        await Seed("old", expiresIn: TimeSpan.FromMinutes(2000));
        Fake.RenewFault = Fail(404);
        Fake.CreateFault = Fail(500);

        var ok = await _service.EnsureAsync(default);

        Assert.False(ok);
        var row = Assert.Single(await _store.ListAsync(default));
        Assert.Equal("old", row.Id);
        Assert.Equal(SubscriptionStatuses.Failed, row.Status);
        var messages = await ErrorMessages();
        Assert.Contains("subscription renewal failed", messages);
        Assert.Contains("subscription ensure failed", messages);
    }

    [Fact]
    public async Task Ensure_ExpiredRow_IsReplaced()
    {
        await Seed("expired", expiresIn: TimeSpan.FromMinutes(-1));

        var ok = await _service.EnsureAsync(default);

        Assert.True(ok);
        Assert.Contains("delete:expired", Fake.SubscriptionCalls);
        Assert.Contains("create:" + CallRecords, Fake.SubscriptionCalls);
        Assert.Equal("graph-sub-1", Assert.Single(await _store.ListAsync(default)).Id);
    }

    [Fact]
    public async Task Ensure_ExtraRowsOfKind_AreRemoved()
    {
        await Seed("older", expiresIn: TimeSpan.FromMinutes(3500));
        await Seed("latest", expiresIn: TimeSpan.FromMinutes(4000));

        var ok = await _service.EnsureAsync(default);

        Assert.True(ok);
        Assert.Equal(new[] { "delete:older" }, Fake.SubscriptionCalls);
        Assert.Equal("latest", Assert.Single(await _store.ListAsync(default)).Id);
    }

    [Fact]
    public async Task Ensure_DeleteInGraphFails_RowIsStillRemoved()
    {
        await Seed("expired", expiresIn: TimeSpan.FromMinutes(-1));
        Fake.DeleteFault = Fail(500);

        var ok = await _service.EnsureAsync(default);

        Assert.True(ok);
        Assert.Equal("graph-sub-1", Assert.Single(await _store.ListAsync(default)).Id);
    }

    // ---- Ensure: errors and URL change (ADR-0001 DV-1) ---------------------------------------------------

    [Fact]
    public async Task Ensure_CreateFails_ReturnsFalse_AndLogs()
    {
        Fake.CreateFault = Fail(500);

        var ok = await _service.EnsureAsync(default);

        Assert.False(ok);
        Assert.Contains("subscription ensure failed", await ErrorMessages());
        Assert.Empty(await _store.ListAsync(default));
        Assert.Empty(_marker.Marked);
    }

    [Fact]
    public async Task Ensure_Create409_IsLoggedNotThrown()
    {
        Fake.CreateFault = Fail(409);

        var ok = await _service.EnsureAsync(default);

        Assert.False(ok);
        var error = Assert.Single(await _env.ErrorRows());
        Assert.Contains("409", error.Details);
    }

    [Fact]
    public async Task Ensure_NotificationUrlChanged_NoGraphCalls_NoDeletion_ReturnsFalse()
    {
        await Seed("sub-old-url", url: "https://old-tunnel.example.test/graph/notify");
        await Seed("expired", expiresIn: TimeSpan.FromMinutes(-10));

        var ok = await _service.EnsureAsync(default);

        Assert.False(ok);
        Assert.Empty(Fake.SubscriptionCalls);
        Assert.Equal(2, (await _store.ListAsync(default)).Count);
        Assert.Contains("subscription notification URL mismatch", await ErrorMessages());
    }

    [Fact]
    public async Task Ensure_MismatchedRowNextToMatchingBest_LogsAndChangesNothing()
    {
        await Seed("matching", expiresIn: TimeSpan.FromMinutes(2000));
        await Seed("other-url", expiresIn: TimeSpan.FromMinutes(3800), url: "https://old-tunnel.example.test/graph/notify");

        var ok = await _service.EnsureAsync(default);

        Assert.False(ok);
        Assert.Empty(Fake.SubscriptionCalls);
        Assert.Equal(new[] { "matching", "other-url" }, (await _store.ListAsync(default)).Select(r => r.Id).OrderBy(x => x).ToArray());
        Assert.Contains("subscription notification URL mismatch", await ErrorMessages());
    }

    [Fact]
    public async Task Ensure_FailedRowWithOtherUrl_IsNotDeleted()
    {
        await Seed("failed-old-url", status: SubscriptionStatuses.Failed, url: "https://old-tunnel.example.test/graph/notify");

        var ok = await _service.EnsureAsync(default);

        Assert.False(ok);
        Assert.Empty(Fake.SubscriptionCalls);
        Assert.Equal("failed-old-url", Assert.Single(await _store.ListAsync(default)).Id);
        Assert.Contains("subscription notification URL mismatch", await ErrorMessages());
    }

    [Fact]
    public async Task Ensure_ExpiredRowWithOtherUrl_IsReplaced()
    {
        await Seed("expired-old-url", expiresIn: TimeSpan.FromMinutes(-1), url: "https://old-tunnel.example.test/graph/notify");

        var ok = await _service.EnsureAsync(default);

        Assert.True(ok);
        Assert.Contains("delete:expired-old-url", Fake.SubscriptionCalls);
        Assert.Equal("graph-sub-1", Assert.Single(await _store.ListAsync(default)).Id);
    }

    // ---- Lifecycle ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Lifecycle_ReauthorizationRequired_Renews()
    {
        await Seed("sub-1", expiresIn: TimeSpan.FromMinutes(4000), status: SubscriptionStatuses.Renewing);
        SeedGraph("sub-1", TimeSpan.FromMinutes(4000));

        await _service.HandleLifecycleAsync("sub-1", "reauthorizationRequired", default);

        Assert.Equal(new[] { "renew:sub-1" }, Fake.SubscriptionCalls);
        var row = Assert.Single(await _store.ListAsync(default));
        Assert.Equal(SubscriptionStatuses.Active, row.Status);
        Assert.Equal(UtcText.Format(Now + Full), row.ExpiresUtc);
    }

    [Fact]
    public async Task Lifecycle_ReauthorizationRequired_KindNotDesired_OnlyLogs()
    {
        await Seed("chan", kind: SubscriptionKinds.ChannelMessages, resource: "teams/t/channels/c/messages");

        await _service.HandleLifecycleAsync("chan", "reauthorizationRequired", default);

        Assert.Empty(Fake.SubscriptionCalls);
        Assert.Equal(SubscriptionStatuses.Active, Assert.Single(await _store.ListAsync(default)).Status);
    }

    [Fact]
    public async Task Lifecycle_SubscriptionRemoved_Recreates()
    {
        await Seed("gone");

        await _service.HandleLifecycleAsync("gone", "subscriptionRemoved", default);

        Assert.Equal(new[] { "list", "create:" + CallRecords }, Fake.SubscriptionCalls);
        var row = Assert.Single(await _store.ListAsync(default));
        Assert.Equal("graph-sub-1", row.Id);
        Assert.Equal(SubscriptionStatuses.Active, row.Status);
    }

    [Fact]
    public async Task Lifecycle_Missed_EnqueuesFullReconcile()
    {
        await Seed("sub-1");

        await _service.HandleLifecycleAsync("sub-1", "missed", default);

        var item = Assert.Single(await _env.InboxRows());
        Assert.Equal(InboxKinds.Reconcile, item.Kind);
        Assert.Null(item.CallId);
        Assert.Equal(TestDb.Tenant, item.TenantId);
        Assert.Equal("{\"reason\":\"missed\",\"subscription_id\":\"sub-1\",\"at\":\"" + UtcText.Format(Now) + "\"}", item.Raw);
        Assert.Empty(Fake.SubscriptionCalls);
    }

    [Fact]
    public async Task Lifecycle_UnknownSubscription_OnlyLogs()
    {
        await _service.HandleLifecycleAsync("nobody", "subscriptionRemoved", default);

        Assert.Empty(await _store.ListAsync(default));
        Assert.Empty(Fake.SubscriptionCalls);
        Assert.Empty(await _env.InboxRows());
    }

    [Fact]
    public async Task Lifecycle_UnknownEvent_OnlyLogs()
    {
        await Seed("sub-1");

        await _service.HandleLifecycleAsync("sub-1", "somethingNew", default);

        Assert.Empty(Fake.SubscriptionCalls);
        Assert.Equal(SubscriptionStatuses.Active, Assert.Single(await _store.ListAsync(default)).Status);
        Assert.Empty(await _env.InboxRows());
    }

    [Fact]
    public async Task Lifecycle_GraphError_IsLoggedNotThrown()
    {
        await Seed("sub-1", expiresIn: TimeSpan.FromMinutes(4000));
        Fake.RenewFault = Fail(500);
        Fake.CreateFault = Fail(500);

        await _service.HandleLifecycleAsync("sub-1", "reauthorizationRequired", default);

        var messages = await ErrorMessages();
        Assert.Contains("subscription renewal failed", messages);
        Assert.Contains("subscription lifecycle handling failed", messages);
    }
}
