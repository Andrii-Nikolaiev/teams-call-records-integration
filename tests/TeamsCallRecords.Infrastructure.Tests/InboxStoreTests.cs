using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TeamsCallRecords.Infrastructure.Config;
using TeamsCallRecords.Infrastructure.Data;
using TeamsCallRecords.Infrastructure.Data.Entities;
using TeamsCallRecords.Infrastructure.Queue;
using TeamsCallRecords.Infrastructure.Tests.Support;

namespace TeamsCallRecords.Infrastructure.Tests;

public class InboxStoreTests
{
    private static InboxStore Store(TestDb db, int maxPending = 10000) =>
        new(db.Gate, db.Factory, Options.Create(new InboxOptions { MaxPending = maxPending }),
            Options.Create(new ProcessingOptions()), db.Clock);

    private static string At(int minutes, int seconds = 0) =>
        UtcText.Format(TestDb.T0.AddMinutes(minutes).AddSeconds(seconds));

    private static InboxNewItem Item(string raw = "{\"n\":1}", string? callId = "call-1", string kind = InboxKinds.CallRecord) =>
        new(kind, TestDb.Tenant, callId, raw);

    private static async Task<long> Seed(TestDb db, string state, string next, string? callId = null, string? locked = null,
        int attempts = 0, string raw = "{\"seed\":1}", string? received = null)
    {
        var row = new InboxRow
        {
            ElementHash = InboxStore.ElementHash(InboxKinds.CallRecord, TestDb.Tenant, raw),
            Kind = InboxKinds.CallRecord,
            TenantId = TestDb.Tenant,
            CallId = callId,
            Raw = raw,
            State = state,
            Attempts = attempts,
            NextAttemptUtc = next,
            LockedUntilUtc = locked,
            ReceivedUtc = received ?? next,
        };
        await db.Gate.RunAsync(async (ctx, ct) => { ctx.InboxItems.Add(row); await ctx.SaveChangesAsync(ct); }, default);
        return row.Id;
    }

    private static Task<List<InboxRow>> Rows(TestDb db) =>
        db.ReadAsync(c => c.InboxItems.AsNoTracking().OrderBy(r => r.Id).ToListAsync());

    // ---- Enqueue ----

    [Fact]
    public async Task Enqueue_Empty_ReturnsZeros()
    {
        using var db = TestDb.Create();
        var r = await Store(db).EnqueueAsync(Array.Empty<InboxNewItem>(), default);
        Assert.Equal(new InboxEnqueueResult(0, 0, false), r);
        Assert.Empty(await Rows(db));
    }

    [Fact]
    public async Task Enqueue_NewItem_IsAdded()
    {
        using var db = TestDb.Create();
        var r = await Store(db).EnqueueAsync(new[] { Item("{\"a\": 1}") }, default);
        Assert.Equal(1, r.Added);
        var row = Assert.Single(await Rows(db));
        Assert.Equal(InboxStates.New, row.State);
        Assert.Equal(0, row.Attempts);
        Assert.Equal(At(0), row.NextAttemptUtc);
        Assert.Equal(At(0), row.ReceivedUtc);
        Assert.Equal("{\"a\": 1}", row.Raw);
    }

    [Fact]
    public async Task Enqueue_SameContent_IsDuplicate()
    {
        using var db = TestDb.Create();
        var store = Store(db);
        await store.EnqueueAsync(new[] { Item() }, default);
        var r = await store.EnqueueAsync(new[] { Item() }, default);
        Assert.Equal(new InboxEnqueueResult(0, 1, false), r);
        Assert.Single(await Rows(db));
    }

    [Fact]
    public async Task Enqueue_PropertyOrderDoesNotMatter()
    {
        using var db = TestDb.Create();
        Assert.Equal(
            InboxStore.ElementHash(InboxKinds.CallRecord, TestDb.Tenant, "{\"a\":1,\"b\":2}"),
            InboxStore.ElementHash(InboxKinds.CallRecord, TestDb.Tenant, "{\"b\":2,\"a\":1}"));
        var store = Store(db);
        await store.EnqueueAsync(new[] { Item("{\"a\":1,\"b\":2}") }, default);
        var r = await store.EnqueueAsync(new[] { Item("{\"b\":2,\"a\":1}") }, default);
        Assert.Equal(1, r.Duplicates);
    }

    [Fact]
    public async Task Enqueue_SameBatchDuplicates()
    {
        using var db = TestDb.Create();
        var r = await Store(db).EnqueueAsync(new[] { Item(), Item() }, default);
        Assert.Equal(new InboxEnqueueResult(1, 1, false), r);
        Assert.Single(await Rows(db));
    }

    [Theory]
    [InlineData(InboxStates.Failed)]
    [InlineData(InboxStates.Done)]
    [InlineData(InboxStates.Processing)]
    public async Task Enqueue_WhenEarlierRowIsNotNew_AddsNewItem(string state)
    {
        using var db = TestDb.Create();
        var raw = "{\"n\":1}";
        await Seed(db, state, At(-5), "call-1", locked: state == InboxStates.Processing ? At(2) : null,
            attempts: state == InboxStates.Failed ? 10 : 1, raw: raw);

        var r = await Store(db).EnqueueAsync(new[] { Item(raw) }, default);

        Assert.Equal(new InboxEnqueueResult(1, 0, false), r);
        var rows = await Rows(db);
        Assert.Equal(2, rows.Count);
        Assert.Equal(state, rows[0].State);
        Assert.Equal(InboxStates.New, rows[1].State);
        Assert.Equal(0, rows[1].Attempts);
    }

    [Fact]
    public async Task Enqueue_Overloaded_WritesNothing()
    {
        using var db = TestDb.Create();
        await Seed(db, InboxStates.New, At(0), raw: "{\"x\":1}");
        await Seed(db, InboxStates.Processing, At(0), locked: At(2), raw: "{\"x\":2}");
        var r = await Store(db, maxPending: 2).EnqueueAsync(new[] { Item("{\"x\":3}") }, default);
        Assert.Equal(new InboxEnqueueResult(0, 0, true), r);
        Assert.Equal(2, (await Rows(db)).Count);
    }

    // ---- ElementHash ----

    [Fact]
    public void ElementHash_Is64LowerHex()
    {
        var h = InboxStore.ElementHash(InboxKinds.CallRecord, TestDb.Tenant, "{\"a\":1}");
        Assert.Matches("^[0-9a-f]{64}$", h);
    }

    [Fact]
    public void ElementHash_DependsOnKindAndTenant()
    {
        const string raw = "{\"a\":1}";
        var hashes = new[]
        {
            InboxStore.ElementHash(InboxKinds.CallRecord, TestDb.Tenant, raw),
            InboxStore.ElementHash(InboxKinds.Reconcile, TestDb.Tenant, raw),
            InboxStore.ElementHash(InboxKinds.CallRecord, "22222222-2222-4222-8222-222222222222", raw),
            InboxStore.ElementHash(InboxKinds.Reconcile, "22222222-2222-4222-8222-222222222222", raw),
        };
        Assert.Equal(4, hashes.Distinct().Count());
    }

    [Fact]
    public void ElementHash_BadJson_Throws() =>
        Assert.Throws<ArgumentException>(() => InboxStore.ElementHash(InboxKinds.CallRecord, TestDb.Tenant, "{ not json"));

    // ---- ClaimNext ----

    [Fact]
    public async Task ClaimNext_ReturnsOldestDue()
    {
        using var db = TestDb.Create();
        await Seed(db, InboxStates.New, At(-1), raw: "{\"i\":1}");
        var older = await Seed(db, InboxStates.New, At(-2), raw: "{\"i\":2}");

        var item = await Store(db).ClaimNextAsync(default);

        Assert.NotNull(item);
        Assert.Equal(older, item!.Id);
        Assert.Equal(1, item.Attempts);
        var row = (await Rows(db)).Single(r => r.Id == older);
        Assert.Equal(InboxStates.Processing, row.State);
        Assert.Equal(At(2), row.LockedUntilUtc);
    }

    [Fact]
    public async Task ClaimNext_SameTimeOrderedById()
    {
        using var db = TestDb.Create();
        var first = await Seed(db, InboxStates.New, At(-1), raw: "{\"i\":1}");
        await Seed(db, InboxStates.New, At(-1), raw: "{\"i\":2}");
        var item = await Store(db).ClaimNextAsync(default);
        Assert.Equal(first, item!.Id);
    }

    [Fact]
    public async Task ClaimNext_SameCallInProcessing_IsSkipped()
    {
        using var db = TestDb.Create();
        var a = await Seed(db, InboxStates.Processing, At(-5), "X", locked: At(2), raw: "{\"i\":1}");
        var b = await Seed(db, InboxStates.New, At(-4), "X", raw: "{\"i\":2}");
        var c = await Seed(db, InboxStates.New, At(-3), "Y", raw: "{\"i\":3}");
        var store = Store(db);

        var first = await store.ClaimNextAsync(default);
        Assert.Equal(c, first!.Id);

        await store.CompleteAsync(a, default);
        var second = await store.ClaimNextAsync(default);
        Assert.Equal(b, second!.Id);
    }

    [Fact]
    public async Task ClaimNext_NullCallId_DoesNotBlock()
    {
        using var db = TestDb.Create();
        await Seed(db, InboxStates.Processing, At(-5), callId: null, locked: At(2), raw: "{\"i\":1}");
        var b = await Seed(db, InboxStates.New, At(-4), callId: null, raw: "{\"i\":2}");
        var item = await Store(db).ClaimNextAsync(default);
        Assert.Equal(b, item!.Id);
    }

    [Fact]
    public async Task ClaimNext_SameCallExpiredLease_DoesNotBlock()
    {
        using var db = TestDb.Create();
        var a = await Seed(db, InboxStates.Processing, At(-5), "X", locked: At(-1), raw: "{\"i\":1}");
        await Seed(db, InboxStates.New, At(-4), "X", raw: "{\"i\":2}");
        var item = await Store(db).ClaimNextAsync(default);
        Assert.Equal(a, item!.Id);
    }

    [Fact]
    public async Task ClaimNext_NothingDue_ReturnsNull()
    {
        using var db = TestDb.Create();
        await Seed(db, InboxStates.New, At(0, 1));
        Assert.Null(await Store(db).ClaimNextAsync(default));
        Assert.Equal(InboxStates.New, (await Rows(db)).Single().State);
    }

    [Fact]
    public async Task ClaimNext_SkipsLockedProcessing()
    {
        using var db = TestDb.Create(TestDb.T0.AddMinutes(1).AddSeconds(59));
        await Seed(db, InboxStates.Processing, At(-5), locked: At(2));
        Assert.Null(await Store(db).ClaimNextAsync(default));
    }

    [Fact]
    public async Task ClaimNext_RecoversExpiredLease()
    {
        using var db = TestDb.Create(TestDb.T0.AddMinutes(2).AddSeconds(1));
        var id = await Seed(db, InboxStates.Processing, At(-5), locked: At(2), attempts: 1);
        var item = await Store(db).ClaimNextAsync(default);
        Assert.Equal(id, item!.Id);
        Assert.Equal(2, item.Attempts);
        Assert.Equal(At(4, 1), (await Rows(db)).Single().LockedUntilUtc);
    }

    [Fact]
    public async Task ClaimNext_ParallelCallers_NoDuplicate()
    {
        using var db = TestDb.Create();
        for (var i = 0; i < 5; i++) await Seed(db, InboxStates.New, At(-1), raw: "{\"i\":" + i + "}");
        var store = Store(db);

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => store.ClaimNextAsync(default)));

        var claimed = results.Where(r => r != null).Select(r => r!.Id).ToList();
        Assert.Equal(5, claimed.Count);
        Assert.Equal(5, claimed.Distinct().Count());
        Assert.Equal(7, results.Count(r => r == null));
    }

    // ---- Complete / Reschedule / Fail ----

    [Fact]
    public async Task Complete_SetsDone()
    {
        using var db = TestDb.Create();
        await Seed(db, InboxStates.New, At(0));
        var store = Store(db);
        var item = await store.ClaimNextAsync(default);
        db.Clock.Advance(TimeSpan.FromSeconds(30));
        await store.CompleteAsync(item!.Id, default);

        var row = (await Rows(db)).Single();
        Assert.Equal(InboxStates.Done, row.State);
        Assert.Equal(At(0, 30), row.DoneUtc);
        Assert.Null(row.LockedUntilUtc);
        Assert.Null(row.LastError);
    }

    [Fact]
    public async Task Reschedule_ResetAttemptsFlag()
    {
        using var db = TestDb.Create();
        var id1 = await Seed(db, InboxStates.Processing, At(-5), locked: At(2), attempts: 3, raw: "{\"i\":1}");
        var id2 = await Seed(db, InboxStates.Processing, At(-5), locked: At(2), attempts: 3, raw: "{\"i\":2}");
        var store = Store(db);

        await store.RescheduleAsync(id1, TestDb.T0.AddMinutes(5), "boom", resetAttempts: true, default);
        await store.RescheduleAsync(id2, TestDb.T0.AddMinutes(5), "boom", resetAttempts: false, default);

        var rows = await Rows(db);
        Assert.All(rows, r =>
        {
            Assert.Equal(InboxStates.New, r.State);
            Assert.Equal(At(5), r.NextAttemptUtc);
            Assert.Equal("boom", r.LastError);
            Assert.Null(r.LockedUntilUtc);
        });
        Assert.Equal(0, rows.Single(r => r.Id == id1).Attempts);
        Assert.Equal(3, rows.Single(r => r.Id == id2).Attempts);
    }

    [Fact]
    public async Task Reschedule_WhenDuplicateNew_MergesWithoutLostRetry()
    {
        using var db = TestDb.Create();
        const string raw = "{\"same\":1}";
        var a = await Seed(db, InboxStates.Processing, At(-5), "X", locked: At(2), attempts: 2, raw: raw);
        var b = await Seed(db, InboxStates.New, At(10), "X", raw: raw);

        await Store(db).RescheduleAsync(a, TestDb.T0.AddMinutes(5), "retry", false, default);

        var rows = await Rows(db);
        var rowA = rows.Single(r => r.Id == a);
        var rowB = rows.Single(r => r.Id == b);
        Assert.Equal(InboxStates.Done, rowA.State);
        Assert.Equal(InboxStates.New, rowB.State);
        Assert.Equal(At(5), rowB.NextAttemptUtc);
        Assert.Single(rows, r => r.State == InboxStates.New);
    }

    [Fact]
    public async Task Fail_SetsFailed()
    {
        using var db = TestDb.Create();
        var id = await Seed(db, InboxStates.Processing, At(-5), locked: At(2));
        await Store(db).FailAsync(id, "bad", default);
        var row = (await Rows(db)).Single();
        Assert.Equal(InboxStates.Failed, row.State);
        Assert.Equal(At(0), row.DoneUtc);
        Assert.Equal("bad", row.LastError);
        Assert.Null(row.LockedUntilUtc);
    }

    [Fact]
    public async Task Transition_MissingId_Throws()
    {
        using var db = TestDb.Create();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store(db).CompleteAsync(999, default));
    }

    // ---- Stats ----

    [Fact]
    public async Task GetStats_CountsPendingOldestAndFailed()
    {
        using var db = TestDb.Create();
        await Seed(db, InboxStates.New, At(-10), raw: "{\"i\":1}");
        await Seed(db, InboxStates.New, At(-5), raw: "{\"i\":2}");
        await Seed(db, InboxStates.Processing, At(-3), locked: At(2), raw: "{\"i\":3}");
        await Seed(db, InboxStates.Failed, At(-20), raw: "{\"i\":4}");
        await Seed(db, InboxStates.Done, At(-30), raw: "{\"i\":5}");

        Assert.Equal(new InboxStats(3, 600, 1), await Store(db).GetStatsAsync(default));
    }

    [Fact]
    public async Task GetStats_Empty_IsZero()
    {
        using var db = TestDb.Create();
        Assert.Equal(new InboxStats(0, 0, 0), await Store(db).GetStatsAsync(default));
    }

    // ---- RenewLease ----

    [Fact]
    public async Task RenewLease_ExtendsOnlyProcessing()
    {
        using var db = TestDb.Create();
        await Seed(db, InboxStates.New, At(0), raw: "{\"i\":1}");
        var store = Store(db);
        var item = await store.ClaimNextAsync(default);
        db.Clock.Advance(TimeSpan.FromSeconds(40));

        var renewed = await store.RenewLeaseAsync(item!.Id, TestDb.T0.AddSeconds(120), default);

        Assert.Equal(TestDb.T0.AddSeconds(160), renewed);
        Assert.Equal(At(2, 40), (await Rows(db)).Single().LockedUntilUtc);

        var n = await Seed(db, InboxStates.New, At(0), raw: "{\"i\":2}");
        var d = await Seed(db, InboxStates.Done, At(0), raw: "{\"i\":3}");
        var f = await Seed(db, InboxStates.Failed, At(0), raw: "{\"i\":4}");
        foreach (var id in new[] { n, d, f })
            Assert.Null(await store.RenewLeaseAsync(id, TestDb.T0.AddSeconds(120), default));
    }

    [Fact]
    public async Task RenewLease_AfterReclaim_Fails()
    {
        using var db = TestDb.Create();
        await Seed(db, InboxStates.New, At(0));
        var store = Store(db);
        var a = await store.ClaimNextAsync(default);
        var staleDeadline = TestDb.T0.AddSeconds(120);

        db.Clock.Advance(TimeSpan.FromSeconds(121));
        var b = await store.ClaimNextAsync(default);
        Assert.Equal(a!.Id, b!.Id);

        Assert.Null(await store.RenewLeaseAsync(a.Id, staleDeadline, default));
        var row = (await Rows(db)).Single();
        Assert.Equal(InboxStates.Processing, row.State);
        Assert.Equal(At(4, 1), row.LockedUntilUtc);
        Assert.Equal(2, row.Attempts);
    }
}
