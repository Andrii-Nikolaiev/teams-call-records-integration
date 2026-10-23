using Microsoft.EntityFrameworkCore;
using TeamsCallRecords.Core;
using TeamsCallRecords.Infrastructure.Data;
using TeamsCallRecords.Infrastructure.Data.Entities;
using TeamsCallRecords.Infrastructure.Processing;
using TeamsCallRecords.Infrastructure.Tests.Support;

namespace TeamsCallRecords.Infrastructure.Tests;

public class SourceStateStoreTests
{
    private static string T(DateTimeOffset t) => UtcText.Format(t);
    private static readonly DateTimeOffset T0 = TestDb.T0;

    [Fact]
    public async Task RegisterIncomplete_First_SetsPending()
    {
        using var env = new Env();
        var item = await env.EnqueueAndClaim();

        var r = await env.States.RegisterIncompleteAsync(TestDb.Tenant, "call-1", "NO_SESSIONS", item.Id, default);

        Assert.Equal(new PendingResult("pending", T0.AddMinutes(1)), r);
        var s = Assert.Single(await env.StateRows());
        Assert.Equal("pending", s.State);
        Assert.Equal(1, s.Attempts);
        Assert.Equal(T(T0), s.FirstSeenUtc);
        Assert.Equal("NO_SESSIONS", s.LastError);
        var inbox = Assert.Single(await env.InboxRows());
        Assert.Equal("new", inbox.State);
        Assert.Equal(T(T0.AddMinutes(1)), inbox.NextAttemptUtc);
    }

    [Fact]
    public async Task RegisterIncomplete_Schedule_1_5_15_60_60()
    {
        using var env = new Env();
        var deltas = new List<double>();
        for (var i = 0; i < 7; i++)
        {
            var item = await env.EnqueueAndClaim();
            var before = env.Now;
            var r = await env.States.RegisterIncompleteAsync(TestDb.Tenant, "call-1", "NO_SESSIONS", item.Id, default);
            Assert.Equal("pending", r.State);
            deltas.Add((r.NextAttemptUtc!.Value - before).TotalMinutes);
            env.Advance(r.NextAttemptUtc.Value - before);
        }
        Assert.Equal(new double[] { 1, 5, 15, 60, 60, 60, 60 }, deltas);
    }

    [Fact]
    public async Task RegisterIncomplete_BecomesSuspendedAtLimit()
    {
        using var env = new Env(T0.AddHours(4));
        await env.SeedState("call-1", "pending", attempts: 7, firstSeen: T0);
        var item = await env.EnqueueAndClaim();

        var r = await env.States.RegisterIncompleteAsync(TestDb.Tenant, "call-1", "NO_SESSIONS", item.Id, default);

        Assert.Equal(new PendingResult("suspended", new DateTimeOffset(2026, 10, 21, 22, 0, 0, TimeSpan.Zero)), r);
        Assert.Equal("done", (await env.InboxRows()).Single().State);
        Assert.Equal("suspended", (await env.StateRows()).Single().State);
    }

    [Fact]
    public async Task RegisterIncomplete_BelowLimit_StaysPending()
    {
        using var env = new Env(T0.AddHours(4).AddSeconds(-1));
        await env.SeedState("call-1", "pending", attempts: 7, firstSeen: T0);
        var item = await env.EnqueueAndClaim();
        var r = await env.States.RegisterIncompleteAsync(TestDb.Tenant, "call-1", "NO_SESSIONS", item.Id, default);
        Assert.Equal("pending", r.State);
    }

    [Fact]
    public async Task RegisterIncomplete_SuspendedRecheck_Reschedules()
    {
        using var env = new Env(T0.AddDays(5));
        await env.SeedState("call-1", "suspended", attempts: 8, firstSeen: T0, next: T0.AddDays(5));
        var item = await env.EnqueueAndClaim();

        var r = await env.States.RegisterIncompleteAsync(TestDb.Tenant, "call-1", "NO_SESSIONS", item.Id, default);

        Assert.Equal(new PendingResult("suspended", T0.AddDays(6)), r);
        Assert.Equal("done", (await env.InboxRows()).Single().State);
        Assert.Empty(await env.ErrorRows());
    }

    [Fact]
    public async Task RegisterIncomplete_BecomesExpiredAfterRetention()
    {
        using var env = new Env(T0.AddDays(30).AddMinutes(1));
        await env.SeedState("call-1", "suspended", attempts: 8, firstSeen: T0, next: T0.AddDays(30));
        var item = await env.EnqueueAndClaim();

        var r = await env.States.RegisterIncompleteAsync(TestDb.Tenant, "call-1", "NO_SESSIONS", item.Id, default);

        Assert.Equal(new PendingResult("expired", null), r);
        var s = Assert.Single(await env.StateRows());
        Assert.Equal("expired", s.State);
        Assert.Null(s.NextAttemptUtc);
        var err = Assert.Single(await env.ErrorRows());
        Assert.Equal("processor", err.Component);
        Assert.Equal("source_state expired", err.Message);
        Assert.Equal($"tenant_id={TestDb.Tenant}; call_id=call-1", err.Details);
    }

    [Fact]
    public async Task RegisterIncomplete_AtExactRetention_NotExpired()
    {
        using var env = new Env(T0.AddDays(30));
        await env.SeedState("call-1", "suspended", attempts: 8, firstSeen: T0, next: T0.AddDays(30));
        var item = await env.EnqueueAndClaim();
        var r = await env.States.RegisterIncompleteAsync(TestDb.Tenant, "call-1", "NO_SESSIONS", item.Id, default);
        Assert.Equal("suspended", r.State);
    }

    [Fact]
    public async Task RegisterIncomplete_Expired_StaysExpired()
    {
        using var env = new Env();
        await env.SeedState("call-1", "expired");
        var item = await env.EnqueueAndClaim();
        var r = await env.States.RegisterIncompleteAsync(TestDb.Tenant, "call-1", "NO_SESSIONS", item.Id, default);
        Assert.Equal(new PendingResult("expired", null), r);
        Assert.Equal("expired", (await env.StateRows()).Single().State);
        Assert.Equal("done", (await env.InboxRows()).Single().State);
    }

    [Fact]
    public async Task RegisterIncomplete_NewEpisodeAfterDone()
    {
        using var env = new Env(T0.AddDays(1));
        await env.SeedState("call-1", "done", applied: 4, attempts: 0, firstSeen: T0);
        var item = await env.EnqueueAndClaim();

        await env.States.RegisterIncompleteAsync(TestDb.Tenant, "call-1", "NO_SESSIONS", item.Id, default);

        var s = Assert.Single(await env.StateRows());
        Assert.Equal("pending", s.State);
        Assert.Equal(1, s.Attempts);
        Assert.Equal(T(T0.AddDays(1)), s.FirstSeenUtc);
        Assert.Equal(4L, s.AppliedVersion);
    }

    [Fact]
    public async Task RegisterIncomplete_MissingInbox_RollsBack()
    {
        using var env = new Env();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            env.States.RegisterIncompleteAsync(TestDb.Tenant, "call-1", "NO_SESSIONS", 999, default));
        Assert.Empty(await env.StateRows());
    }

    // ---- RecordRejection ----

    private static IReadOnlyList<Rejection> Rej(string? call = "call-1", string? session = "s1", string code = "SESSION_END_BEFORE_START", string message = "bad") =>
        new[] { new Rejection(call, session, code, message) };

    [Fact]
    public async Task RecordRejection_WritesRejectedAndState()
    {
        using var env = new Env();
        var item = await env.EnqueueAndClaim();

        await env.States.RecordRejectionAsync(TestDb.Tenant, "call-1", 6, Rej(), "hash1", item.Id, default);

        var rej = Assert.Single(await env.RejectedRows());
        Assert.Equal("call-1", rej.CallId);
        Assert.Equal("s1", rej.SessionId);
        Assert.Equal("SESSION_END_BEFORE_START", rej.Code);
        Assert.Equal("hash1", rej.SourceHash);
        var s = Assert.Single(await env.StateRows());
        Assert.Equal("done", s.State);
        Assert.Null(s.AppliedVersion);
        Assert.Equal(6L, s.RejectedVersion);
        Assert.Equal("done", (await env.InboxRows()).Single().State);
    }

    [Fact]
    public async Task RecordRejection_KeepsMaxRejectedVersion()
    {
        using var env = new Env();
        await env.SeedState("call-1", "done", rejected: 7);
        var item = await env.EnqueueAndClaim();
        await env.States.RecordRejectionAsync(TestDb.Tenant, "call-1", 6, Rej(), "h", item.Id, default);
        Assert.Equal(7L, (await env.StateRows()).Single().RejectedVersion);
    }

    [Fact]
    public async Task RecordRejection_SameRowTwice_NotDuplicated()
    {
        using var env = new Env();
        var a = await env.EnqueueAndClaim();
        await env.States.RecordRejectionAsync(TestDb.Tenant, "call-1", 6, Rej(), "h", a.Id, default);
        var b = await env.EnqueueAndClaim();
        await env.States.RecordRejectionAsync(TestDb.Tenant, "call-1", 6, Rej(), "h", b.Id, default);
        Assert.Single(await env.RejectedRows());
    }

    [Fact]
    public async Task RecordRejection_OtherHash_AddsRow()
    {
        using var env = new Env();
        var a = await env.EnqueueAndClaim();
        await env.States.RecordRejectionAsync(TestDb.Tenant, "call-1", 6, Rej(), "h1", a.Id, default);
        var b = await env.EnqueueAndClaim();
        await env.States.RecordRejectionAsync(TestDb.Tenant, "call-1", 6, Rej(), "h2", b.Id, default);
        Assert.Equal(2, (await env.RejectedRows()).Count);
    }

    [Fact]
    public async Task RecordRejection_ClosesPendingEpisode()
    {
        using var env = new Env();
        await env.SeedState("call-1", "pending", attempts: 3, next: T0.AddMinutes(5));
        var item = await env.EnqueueAndClaim();
        await env.States.RecordRejectionAsync(TestDb.Tenant, "call-1", 6, Rej(), "h", item.Id, default);
        var s = (await env.StateRows()).Single();
        Assert.Equal("done", s.State);
        Assert.Equal(0, s.Attempts);
        Assert.Null(s.NextAttemptUtc);
    }

    [Fact]
    public async Task RecordRejection_WithoutCallId_WritesOnlyRejected()
    {
        using var env = new Env();
        var item = await env.EnqueueAndClaim();
        var hash = Canonical.Sha256Hex("{ not json");
        await env.States.RecordRejectionAsync(TestDb.Tenant, null,  null,
            new[] { new Rejection(null, null, RejectionCodes.BadJson, "bad json") }, hash, item.Id, default);

        var rej = Assert.Single(await env.RejectedRows());
        Assert.Equal("0228f717792ffbc02f7fa058c934c10d7e91d0de743e365ebd78bfebf019254e", rej.SourceHash);
        Assert.Null(rej.CallId);
        Assert.Empty(await env.StateRows());
    }

    [Fact]
    public async Task RecordRejection_MessageTruncatedTo1000()
    {
        using var env = new Env();
        var item = await env.EnqueueAndClaim();
        await env.States.RecordRejectionAsync(TestDb.Tenant, "call-1", 6, Rej(message: new string('x', 1500)), "h", item.Id, default);
        Assert.Equal(1000, (await env.RejectedRows()).Single().Message.Length);
    }

    // ---- RecordDiscrepancy ----

    [Fact]
    public async Task RecordDiscrepancy_WritesAuditRow()
    {
        using var env = new Env();
        await env.SeedState("call-1", "done", applied: 4, setHash: "applied");
        var item = await env.EnqueueAndClaim();

        await env.States.RecordDiscrepancyAsync(TestDb.Tenant, "call-1", 4, "applied", "incoming", item.Id, default);

        var a = Assert.Single(await env.AuditRows());
        Assert.Equal("version_discrepancy", a.Kind);
        Assert.Equal("{\"version\":4,\"applied_set_hash\":\"applied\",\"incoming_set_hash\":\"incoming\"}", a.Detail);
        Assert.Equal("done", (await env.InboxRows()).Single().State);
        Assert.Equal(4L, (await env.StateRows()).Single().AppliedVersion);
    }

    [Fact]
    public async Task RecordDiscrepancy_NullAppliedHash_WritesJsonNull()
    {
        using var env = new Env();
        var item = await env.EnqueueAndClaim();
        await env.States.RecordDiscrepancyAsync(TestDb.Tenant, "call-1", 4, null, "incoming", item.Id, default);
        Assert.Contains("\"applied_set_hash\":null", (await env.AuditRows()).Single().Detail);
    }

    // ---- Reads ----

    [Fact]
    public async Task GetMany_ChunksOf500()
    {
        using var env = new Env();
        await env.Db.Gate.RunAsync(async (ctx, ct) =>
        {
            var now = T(T0);
            for (var i = 0; i < 1200; i++)
                ctx.SourceStates.Add(new SourceStateRow { TenantId = TestDb.Tenant, CallId = "c" + i, State = "done", FirstSeenUtc = now, UpdatedUtc = now });
            await ctx.SaveChangesAsync(ct);
        }, default);

        var ids = Enumerable.Range(0, 1200).Select(i => "c" + i).ToList();
        var many = await env.States.GetManyAsync(TestDb.Tenant, ids, default);
        Assert.Equal(1200, many.Count);
    }

    [Fact]
    public async Task ListSuspendedDue_OrdersAndLimits()
    {
        using var env = new Env(T0.AddMinutes(5));
        await env.SeedState("a", "suspended", next: T0.AddMinutes(3));
        await env.SeedState("b", "suspended", next: T0.AddMinutes(1));
        await env.SeedState("c", "suspended", next: T0.AddMinutes(9));

        var one = await env.States.ListSuspendedDueAsync(1, default);
        Assert.Equal(new[] { "b" }, one.Select(r => r.CallId));
        var ten = await env.States.ListSuspendedDueAsync(10, default);
        Assert.Equal(new[] { "b", "a" }, ten.Select(r => r.CallId));
    }

    [Fact]
    public async Task GetCounts_GroupsByState()
    {
        using var env = new Env();
        await env.SeedState("p1", "pending");
        await env.SeedState("p2", "pending");
        await env.SeedState("s1", "suspended");
        await env.SeedState("e1", "expired");
        await env.SeedState("d1", "done");
        await env.SeedState("d2", "done");
        await env.SeedState("d3", "done");
        Assert.Equal(new SourceStateCounts(2, 1, 1), await env.States.GetCountsAsync(default));
    }
}
