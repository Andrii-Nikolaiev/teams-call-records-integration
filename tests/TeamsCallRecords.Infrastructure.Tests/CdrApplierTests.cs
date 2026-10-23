using TeamsCallRecords.Core;
using TeamsCallRecords.Infrastructure.Data;
using TeamsCallRecords.Infrastructure.Processing;
using TeamsCallRecords.Infrastructure.Queue;
using TeamsCallRecords.Infrastructure.Tests.Support;

namespace TeamsCallRecords.Infrastructure.Tests;

public class CdrApplierTests
{
    private static ApplyRequest Req(InboxItem item, string record) =>
        new(TestDb.Tenant, CallRecordConverter.Convert(record, TestDb.Tenant), item.Id);

    private static string TwoSessions(long version = 3) =>
        GraphSamples.Record(GraphSamples.Session("s1") + "," + GraphSamples.Session("s2", caller: "user-c", callee: "user-d"), version);

    [Fact]
    public async Task Apply_InsertsCdrStateOutbox()
    {
        using var env = new Env();
        var item = await env.EnqueueAndClaim();

        var r = await env.Applier.ApplyAsync(Req(item, GraphSamples.Simple()), default);

        Assert.Equal(new ApplyResult(ApplyStatus.Applied, null, 1, 0, 0, 2), r);
        var cdr = Assert.Single(await env.CdrRows());
        Assert.Equal("call-1", cdr.CallId);
        Assert.Equal(61, cdr.DurationS);
        var s = Assert.Single(await env.StateRows());
        Assert.Equal(3L, s.AppliedVersion);
        Assert.Equal("done", s.State);
        Assert.Equal(2, (await env.OutboxRows()).Count);
        Assert.Equal("done", (await env.InboxRows()).Single().State);
    }

    [Fact]
    public async Task Apply_TwoSessions_FourOutboxRows()
    {
        using var env = new Env();
        var item = await env.EnqueueAndClaim();
        var r = await env.Applier.ApplyAsync(Req(item, TwoSessions()), default);
        Assert.Equal(2, r.Inserted);
        Assert.Equal(4, r.OutboxCreated);
        Assert.Equal(new long[] { 1, 2 }, (await env.CdrRows()).Select(c => c.CdrSeq));
    }

    [Fact]
    public async Task Apply_NoParties_NoOutbox()
    {
        using var env = new Env();
        var item = await env.EnqueueAndClaim();
        var record = GraphSamples.Record(GraphSamples.Session("s1", caller: "", callee: ""));
        var r = await env.Applier.ApplyAsync(Req(item, record), default);
        Assert.Equal(1, r.Inserted);
        Assert.Equal(0, r.OutboxCreated);
        var cdr = (await env.CdrRows()).Single();
        Assert.Null(cdr.CallerId);
        Assert.Null(cdr.CalleeId);
    }

    [Fact]
    public async Task Apply_FailedCall_StoresReason()
    {
        using var env = new Env();
        var item = await env.EnqueueAndClaim();
        var record = GraphSamples.Record(GraphSamples.Session("s1", failure: "callSetup:busy"));
        await env.Applier.ApplyAsync(Req(item, record), default);
        var cdr = (await env.CdrRows()).Single();
        Assert.Equal("failed", cdr.Status);
        Assert.False(string.IsNullOrEmpty(cdr.TerminationReason));
    }

    [Fact]
    public async Task Apply_KeepsSubSecondTime()
    {
        using var env = new Env();
        var item = await env.EnqueueAndClaim();
        var record = GraphSamples.Record(GraphSamples.Session("s1", "2026-10-20T17:50:00.1234567Z", "2026-10-20T17:51:00.7654321Z"));
        await env.Applier.ApplyAsync(Req(item, record), default);
        var cdr = (await env.CdrRows()).Single();
        Assert.Equal("2026-10-20T17:50:00.1234567Z", cdr.StartUtc);
        Assert.Equal("2026-10-20T17:51:00.7654321Z", cdr.EndUtc);
    }

    [Fact]
    public async Task Apply_HigherVersion_UpdatesInPlace()
    {
        using var env = new Env();
        var first = await env.EnqueueAndClaim();
        await env.Applier.ApplyAsync(Req(first, GraphSamples.Simple(3)), default);
        var before = (await env.CdrRows()).Single();

        env.Advance(TimeSpan.FromMinutes(10));
        var second = await env.EnqueueAndClaim();
        var r = await env.Applier.ApplyAsync(Req(second, GraphSamples.Simple(4)), default);

        Assert.Equal(new ApplyResult(ApplyStatus.Applied, null, 0, 1, 0, 0), r);
        var after = (await env.CdrRows()).Single();
        Assert.Equal(before.CdrSeq, after.CdrSeq);
        Assert.Equal(before.CreatedUtc, after.CreatedUtc);
        Assert.NotEqual(before.UpdatedUtc, after.UpdatedUtc);
        Assert.Equal(4L, after.SourceVersion);
        Assert.Equal(2, (await env.OutboxRows()).Count);
    }

    [Fact]
    public async Task Apply_ReplaceSet_DeletesMissingSession()
    {
        using var env = new Env();
        var first = await env.EnqueueAndClaim();
        await env.Applier.ApplyAsync(Req(first, TwoSessions(3)), default);

        var second = await env.EnqueueAndClaim();
        var r = await env.Applier.ApplyAsync(Req(second, GraphSamples.Record(GraphSamples.Session("s1"), 4)), default);

        Assert.Equal(1, r.Updated);
        Assert.Equal(1, r.Deleted);
        Assert.Equal("s1", Assert.Single(await env.CdrRows()).SessionId);
        Assert.Equal(4, (await env.OutboxRows()).Count);
    }

    [Fact]
    public async Task Apply_OldEndedCall_NoOutbox()
    {
        using var env = new Env();
        var item = await env.EnqueueAndClaim();
        var record = GraphSamples.Record(GraphSamples.Session("s1", "2026-10-19T16:59:00Z", "2026-10-19T17:00:00Z"));
        var r = await env.Applier.ApplyAsync(Req(item, record), default);
        Assert.Equal(0, r.OutboxCreated);
        Assert.Single(await env.CdrRows());
    }

    [Fact]
    public async Task Apply_OutboxBoundary_ExactlyMaxAge()
    {
        using var env = new Env();
        var item = await env.EnqueueAndClaim();
        var record = GraphSamples.Record(GraphSamples.Session("s1", "2026-10-19T17:59:00Z", "2026-10-19T18:00:00Z"));
        var r = await env.Applier.ApplyAsync(Req(item, record), default);
        Assert.Equal(2, r.OutboxCreated);
    }

    [Fact]
    public async Task Apply_SameCallerAndCallee_OneRecipient()
    {
        using var env = new Env();
        var item = await env.EnqueueAndClaim();
        var record = GraphSamples.Record(GraphSamples.Session("s1", caller: "user-a", callee: "user-a"));
        var r = await env.Applier.ApplyAsync(Req(item, record), default);
        Assert.Equal(1, r.OutboxCreated);
    }

    [Fact]
    public async Task Apply_OnlyUserParties_AreRecipients()
    {
        using var env = new Env();
        var item = await env.EnqueueAndClaim();
        var record = GraphSamples.Record(GraphSamples.AppCallerSession("s1"));
        var r = await env.Applier.ApplyAsync(Req(item, record), default);
        Assert.Equal(1, r.OutboxCreated);
        Assert.Equal("user-b", Assert.Single(await env.OutboxRows()).Recipient);
    }

    [Fact]
    public async Task Apply_DecisionChanged_ChangesNothing()
    {
        using var env = new Env();
        await env.SeedState("call-1", "done", applied: 5, setHash: "h");
        var item = await env.EnqueueAndClaim();

        var r = await env.Applier.ApplyAsync(Req(item, GraphSamples.Simple(4)), default);

        Assert.Equal(new ApplyResult(ApplyStatus.DecisionChanged, VersionAction.IgnoreLower, 0, 0, 0, 0), r);
        Assert.Empty(await env.CdrRows());
        Assert.Empty(await env.OutboxRows());
        Assert.Equal("processing", (await env.InboxRows()).Single().State);
    }

    [Fact]
    public async Task Apply_SameVersionTwice_SecondIsDecisionChanged()
    {
        using var env = new Env();
        var a = await env.EnqueueAndClaim();
        await env.Applier.ApplyAsync(Req(a, GraphSamples.Simple(3)), default);
        var b = await env.EnqueueAndClaim();
        var r = await env.Applier.ApplyAsync(Req(b, GraphSamples.Simple(3)), default);
        Assert.Equal(ApplyStatus.DecisionChanged, r.Status);
        Assert.Equal(VersionAction.DuplicateSame, r.ChangedAction);
        Assert.Single(await env.CdrRows());
        Assert.Equal(2, (await env.OutboxRows()).Count);
    }

    [Fact]
    public async Task Apply_Failure_RollsBackEverything()
    {
        using var env = new Env();
        var item = await env.EnqueueAndClaim();
        var bad = new ConversionResult
        {
            CallId = "call-1",
            SourceVersion = 3,
            IsComplete = true,
            SessionSetHash = "h",
            Cdrs = new[] { Builders.Cdr("call-1", "s1"), Builders.Cdr("call-1", "s2", durationSeconds: -1) },
        };

        await Assert.ThrowsAnyAsync<Exception>(() =>
            env.Applier.ApplyAsync(new ApplyRequest(TestDb.Tenant, bad, item.Id), default));

        Assert.Empty(await env.CdrRows());
        Assert.Empty(await env.OutboxRows());
        Assert.Empty(await env.StateRows());
        Assert.Equal("processing", (await env.InboxRows()).Single().State);
    }

    [Fact]
    public async Task Apply_PreservesRejectedVersion()
    {
        using var env = new Env();
        await env.SeedState("call-1", "done", rejected: 3);
        var item = await env.EnqueueAndClaim();
        await env.Applier.ApplyAsync(Req(item, GraphSamples.Simple(5)), default);
        var s = (await env.StateRows()).Single();
        Assert.Equal(3L, s.RejectedVersion);
        Assert.Equal(5L, s.AppliedVersion);
    }

    [Fact]
    public async Task Payload_Format_IsExact()
    {
        using var env = new Env();
        var item = await env.EnqueueAndClaim();
        await env.Applier.ApplyAsync(Req(item, GraphSamples.Simple()), default);
        var payload = (await env.OutboxRows()).First().Payload;
        Assert.Equal("{\"activity_type\":\"callCompleted\",\"call_type\":\"oneToOne\",\"status\":\"completed\",\"duration_s\":61,\"start_utc\":\"2026-10-20T17:50:00.0000000Z\"}", payload);
    }
}
