using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TeamsCallRecords.Infrastructure.Config;
using TeamsCallRecords.Infrastructure.Data.Entities;
using TeamsCallRecords.Infrastructure.Graph;
using TeamsCallRecords.Infrastructure.Processing;
using TeamsCallRecords.Infrastructure.Queue;
using TeamsCallRecords.Infrastructure.Tests.Support;

namespace TeamsCallRecords.Infrastructure.Tests;

public class ReconcilerTests
{
    private static readonly DateTimeOffset From = TestDb.T0.AddHours(-24);

    private static Reconciler Make(Env env, IInboxStore? inbox = null) =>
        new(env.FakeGraph, env.States, inbox ?? env.Inbox, Options.Create(env.Graph), Options.Create(new ReconcileOptions()),
            env.Db.Clock, NullLogger<Reconciler>.Instance);

    private static void GraphHas(Env env, string id, long? version) =>
        env.FakeGraph.OnList = (_, _) => new[] { new GraphCallRecordRef(id, version, null) };

    private static Task<int> Run(Env env, bool force = false) => Make(env).ReconcileAsync(From, TestDb.T0, force, default);

    [Fact]
    public async Task Reconcile_UnknownCall_IsEnqueued()
    {
        using var env = new Env();
        GraphHas(env, "call-1", 3);
        Assert.Equal(1, await Run(env));
        var row = Assert.Single(await env.InboxRows());
        Assert.Equal("reconcile", row.Kind);
        Assert.Equal("call-1", row.CallId);
        Assert.Equal("{\"call_id\":\"call-1\",\"version\":3}", row.Raw);
    }

    [Fact]
    public async Task Reconcile_KnownSameVersion_NotEnqueued()
    {
        using var env = new Env();
        await env.SeedState("call-1", "done", applied: 3);
        GraphHas(env, "call-1", 3);
        Assert.Equal(0, await Run(env));
    }

    [Fact]
    public async Task Reconcile_KnownHigherVersion_IsEnqueued()
    {
        using var env = new Env();
        await env.SeedState("call-1", "done", applied: 3);
        GraphHas(env, "call-1", 4);
        Assert.Equal(1, await Run(env));
    }

    [Fact]
    public async Task Reconcile_VersionNotAboveRejected_NotEnqueued()
    {
        using var env = new Env();
        await env.SeedState("call-1", "done", rejected: 6);
        GraphHas(env, "call-1", 6);
        Assert.Equal(0, await Run(env));
    }

    [Fact]
    public async Task Reconcile_VersionNull_DoneState_IsEnqueued()
    {
        using var env = new Env();
        await env.SeedState("call-1", "done", applied: 3);
        GraphHas(env, "call-1", null);

        Assert.Equal(1, await Run(env));
        Assert.Equal(0, await Run(env));          // the new row has not been processed yet

        var item = await env.Inbox.ClaimNextAsync(default);
        await env.Inbox.CompleteAsync(item!.Id, default);
        Assert.Equal(1, await Run(env));          // processed: enqueued again
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("suspended")]
    [InlineData("expired")]
    public async Task Reconcile_PendingSuspendedExpired_NotEnqueued(string state)
    {
        using var env = new Env();
        await env.SeedState("call-1", state);
        GraphHas(env, "call-1", 9);
        Assert.Equal(0, await Run(env));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("done")]
    [InlineData("pending")]
    [InlineData("suspended")]
    [InlineData("expired")]
    public async Task Reconcile_Force_EnqueuesEveryState(string? state)
    {
        using var env = new Env();
        if (state != null) await env.SeedState("call-1", state, applied: state == "done" ? 3 : null);
        GraphHas(env, "call-1", 3);
        Assert.Equal(1, await Run(env, force: true));
        Assert.Equal("{\"call_id\":\"call-1\",\"version\":3}", (await env.InboxRows()).Single().Raw);
    }

    [Fact]
    public async Task Reconcile_ForceFalse_SameVersionDone_NotEnqueued()
    {
        using var env = new Env();
        await env.SeedState("call-1", "done", applied: 3);
        GraphHas(env, "call-1", 3);
        Assert.Equal(0, await Run(env, force: false));
    }

    [Fact]
    public async Task Reconcile_SecondRun_AddsNothing()
    {
        using var env = new Env();
        GraphHas(env, "call-1", 3);
        Assert.Equal(1, await Run(env));
        Assert.Equal(0, await Run(env));
    }

    [Fact]
    public async Task Reconcile_AfterFailedItem_AddsNewItem()
    {
        using var env = new Env();
        GraphHas(env, "call-1", 3);
        await Run(env);
        var item = await env.Inbox.ClaimNextAsync(default);
        await env.Inbox.FailAsync(item!.Id, "x", default);

        Assert.Equal(1, await Run(env));
        var rows = await env.InboxRows();
        Assert.Equal(2, rows.Count);
        Assert.Equal("failed", rows[0].State);
        Assert.Equal("new", rows[1].State);
    }

    [Fact]
    public async Task Reconcile_Overloaded_ReturnsZero()
    {
        using var env = new Env();
        var small = new InboxStore(env.Db.Gate, env.Db.Factory, Options.Create(new InboxOptions { MaxPending = 1 }),
            Options.Create(env.Processing), env.Db.Clock);
        await small.EnqueueAsync(new[] { new InboxNewItem("channelMessage", TestDb.Tenant, null, "{\"x\":1}") }, default);
        GraphHas(env, "call-1", 3);

        Assert.Equal(0, await Make(env, small).ReconcileAsync(From, TestDb.T0, false, default));
        Assert.Single(await env.InboxRows());
    }

    [Fact]
    public async Task Reconcile_GraphError_Propagates()
    {
        using var env = new Env();
        env.FakeGraph.OnList = (_, _) => throw new GraphException("503", 503, true);
        await Assert.ThrowsAsync<GraphException>(() => Run(env));
        Assert.Empty(await env.InboxRows());
    }

    [Fact]
    public async Task Lookback_UsesNowMinusLookback_AndSetsLastRun()
    {
        using var env = new Env();
        DateTimeOffset? gotFrom = null, gotTo = null;
        env.FakeGraph.OnList = (f, t) => { gotFrom = f; gotTo = t; return Array.Empty<GraphCallRecordRef>(); };
        var reconciler = Make(env);
        Assert.Null(reconciler.LastRunUtc);

        await reconciler.ReconcileLookbackAsync(default);

        Assert.Equal(new DateTimeOffset(2026, 10, 19, 18, 0, 0, TimeSpan.Zero), gotFrom);
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 18, 0, 0, TimeSpan.Zero), gotTo);
        Assert.Equal(TestDb.T0, reconciler.LastRunUtc);

        env.Advance(TimeSpan.FromMinutes(15));
        env.FakeGraph.OnList = (_, _) => throw new GraphException("503", 503, true);
        await Assert.ThrowsAsync<GraphException>(() => reconciler.ReconcileLookbackAsync(default));
        Assert.Equal(TestDb.T0, reconciler.LastRunUtc);
    }

    [Fact]
    public async Task RecheckSuspended_EnqueuesDueRows()
    {
        using var env = new Env();
        await env.SeedState("due", "suspended", next: TestDb.T0.AddMinutes(-1));
        await env.SeedState("later", "suspended", next: TestDb.T0.AddHours(1));

        Assert.Equal(1, await Make(env).RecheckSuspendedAsync(default));

        var row = Assert.Single(await env.InboxRows());
        Assert.Equal("due", row.CallId);
        Assert.Equal("{\"call_id\":\"due\",\"recheck_at\":\"2026-10-20T17:59:00.0000000Z\"}", row.Raw);
    }

    [Fact]
    public async Task RecheckSuspended_SameRowTwice_NotDuplicated()
    {
        using var env = new Env();
        await env.SeedState("due", "suspended", next: TestDb.T0.AddMinutes(-1));
        var reconciler = Make(env);
        Assert.Equal(1, await reconciler.RecheckSuspendedAsync(default));
        Assert.Equal(0, await reconciler.RecheckSuspendedAsync(default));
    }
}
