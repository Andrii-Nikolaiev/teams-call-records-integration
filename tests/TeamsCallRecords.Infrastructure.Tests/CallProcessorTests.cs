using Microsoft.EntityFrameworkCore;
using TeamsCallRecords.Core;
using TeamsCallRecords.Infrastructure.Data;
using TeamsCallRecords.Infrastructure.Data.Entities;
using TeamsCallRecords.Infrastructure.Graph;
using TeamsCallRecords.Infrastructure.Processing;
using TeamsCallRecords.Infrastructure.Queue;
using TeamsCallRecords.Infrastructure.Tests.Support;

namespace TeamsCallRecords.Infrastructure.Tests;

public class CallProcessorTests
{
    private static async Task<ProcessOutcome> Run(Env env, string json, string callId = "call-1", ICdrApplier? applier = null)
    {
        env.FakeGraph.Returns(GraphFetchStatus.Ok, json);
        var item = await env.EnqueueAndClaim(callId);
        return await env.Processor(applier).ProcessAsync(item, default);
    }

    [Fact]
    public async Task ChannelMessage_IsLoggedAndCompleted()
    {
        using var env = new Env();
        var item = await env.EnqueueAndClaim(null, InboxKinds.ChannelMessage, "{\"x\":1}");
        Assert.Equal(ProcessOutcome.Logged, await env.Processor().ProcessAsync(item, default));
        Assert.Equal("done", (await env.InboxRows()).Single().State);
    }

    [Fact]
    public async Task CallRecordWithoutCallId_Fails()
    {
        using var env = new Env();
        var item = await env.EnqueueAndClaim(null);
        Assert.Equal(ProcessOutcome.Failed, await env.Processor().ProcessAsync(item, default));
        var row = (await env.InboxRows()).Single();
        Assert.Equal("failed", row.State);
        Assert.Equal("INBOX_NO_CALL_ID", row.LastError);
        Assert.Equal("inbox item without call_id", (await env.ErrorRows()).Single().Message);
    }

    [Fact]
    public async Task Ok_AppliesAndCreatesCdr()
    {
        using var env = new Env();
        Assert.Equal(ProcessOutcome.Applied, await Run(env, GraphSamples.Simple()));
        Assert.Single(await env.CdrRows());
        Assert.Equal("done", (await env.InboxRows()).Single().State);
    }

    [Fact]
    public async Task SameVersionAgain_IsDuplicate()
    {
        using var env = new Env();
        await Run(env, GraphSamples.Simple());
        Assert.Equal(ProcessOutcome.Duplicate, await Run(env, GraphSamples.Simple()));
        Assert.Single(await env.CdrRows());
        Assert.Equal(2, (await env.OutboxRows()).Count);
    }

    [Fact]
    public async Task LowerVersion_IsIgnored()
    {
        using var env = new Env();
        await Run(env, GraphSamples.Simple(5));
        Assert.Equal(ProcessOutcome.IgnoredLower, await Run(env, GraphSamples.Simple(4)));
        Assert.Equal(5L, (await env.StateRows()).Single().AppliedVersion);
    }

    [Fact]
    public async Task SameVersionDifferentContent_IsDiscrepancy()
    {
        using var env = new Env();
        await Run(env, GraphSamples.Simple(5));
        var changed = GraphSamples.Record(GraphSamples.Session("s1", end: "2026-10-20T17:52:00Z"), 5);
        Assert.Equal(ProcessOutcome.Discrepancy, await Run(env, changed));
        Assert.Single(await env.AuditRows());
        Assert.Equal(61, (await env.CdrRows()).Single().DurationS);
    }

    private static string InvalidSession() =>
        GraphSamples.Record(GraphSamples.Session("s1", "2026-10-20T17:55:00Z", "2026-10-20T17:50:00Z"), 6);

    [Fact]
    public async Task InvalidSession_IsRejected_AndRepeatIsDuplicate()
    {
        using var env = new Env();
        Assert.Equal(ProcessOutcome.Rejected, await Run(env, InvalidSession()));
        Assert.Empty(await env.CdrRows());
        var s = (await env.StateRows()).Single();
        Assert.Equal(6L, s.RejectedVersion);
        Assert.Null(s.AppliedVersion);
        Assert.Single(await env.RejectedRows());

        Assert.Equal(ProcessOutcome.Duplicate, await Run(env, InvalidSession()));
        Assert.Single(await env.RejectedRows());
    }

    [Fact]
    public void RejectedSourceHash_IsCanonical()
    {
        const string a = "{\"id\":\"c\",\"version\":6}";
        const string b = "{ \"version\": 6,\n  \"id\": \"c\" }";
        Assert.Equal(CallProcessor.RejectedSourceHash(a), CallProcessor.RejectedSourceHash(b));
    }

    [Fact]
    public async Task NewerValidVersion_AppliesAfterRejection()
    {
        using var env = new Env();
        await Run(env, InvalidSession());
        Assert.Equal(ProcessOutcome.Applied, await Run(env, GraphSamples.Simple(7)));
        var s = (await env.StateRows()).Single();
        Assert.Equal(6L, s.RejectedVersion);
        Assert.Equal(7L, s.AppliedVersion);
    }

    [Fact]
    public async Task Malformed_RejectedWithRawHash()
    {
        using var env = new Env();
        env.FakeGraph.Returns(GraphFetchStatus.Malformed, "{ not json");
        var item = await env.EnqueueAndClaim();

        Assert.Equal(ProcessOutcome.Rejected, await env.Processor().ProcessAsync(item, default));

        var rej = Assert.Single(await env.RejectedRows());
        Assert.Equal(RejectionCodes.BadJson, rej.Code);
        Assert.Equal("0228f717792ffbc02f7fa058c934c10d7e91d0de743e365ebd78bfebf019254e", rej.SourceHash);
        Assert.Empty(await env.StateRows());
        Assert.Equal("done", (await env.InboxRows()).Single().State);
        Assert.Empty(await env.ErrorRows());
    }

    [Fact]
    public async Task Malformed_Repeated_NoSecondRejectedRow()
    {
        using var env = new Env();
        env.FakeGraph.Returns(GraphFetchStatus.Malformed, "{ not json");
        for (var i = 0; i < 2; i++)
        {
            var item = await env.EnqueueAndClaim();
            Assert.Equal(ProcessOutcome.Rejected, await env.Processor().ProcessAsync(item, default));
        }
        Assert.Single(await env.RejectedRows());
    }

    [Theory]
    [InlineData(GraphFetchStatus.NotFound, "GRAPH_NOT_FOUND")]
    [InlineData(GraphFetchStatus.PagesIncomplete, "GRAPH_PAGES_INCOMPLETE")]
    public async Task GraphNotFoundOrIncomplete_RegistersIncomplete(GraphFetchStatus status, string reason)
    {
        using var env = new Env();
        env.FakeGraph.Returns(status, status == GraphFetchStatus.PagesIncomplete ? "{}" : null);
        var item = await env.EnqueueAndClaim();
        Assert.Equal(ProcessOutcome.Pending, await env.Processor().ProcessAsync(item, default));
        Assert.Equal(reason, (await env.StateRows()).Single().LastError);
        Assert.Empty(await env.CdrRows());
    }

    [Theory]
    [InlineData(GraphFetchStatus.NotFound)]
    [InlineData(GraphFetchStatus.PagesIncomplete)]
    public async Task GraphNotFoundOrIncomplete_AlreadyApplied_Duplicate(GraphFetchStatus status)
    {
        using var env = new Env();
        await Run(env, GraphSamples.Simple(4));
        env.FakeGraph.Returns(status, null);
        var item = await env.EnqueueAndClaim();
        Assert.Equal(ProcessOutcome.Duplicate, await env.Processor().ProcessAsync(item, default));
        Assert.Equal("done", (await env.StateRows()).Single().State);
        Assert.Single(await env.CdrRows());
    }

    [Fact]
    public async Task TransientGraphError_Reschedules()
    {
        using var env = new Env();
        env.FakeGraph.Throws(new GraphException("503", 503, true));
        var item = await env.EnqueueAndClaim();
        Assert.Equal(ProcessOutcome.Retry, await env.Processor().ProcessAsync(item, default));
        var row = (await env.InboxRows()).Single();
        Assert.Equal("new", row.State);
        Assert.Equal(UtcText.Format(env.Now.AddMinutes(1)), row.NextAttemptUtc);
        Assert.Equal(1, row.Attempts);
    }

    [Fact]
    public async Task TransientGraphError_RetryScheduleTable()
    {
        using var env = new Env();
        env.FakeGraph.Throws(new GraphException("503", 503, true));
        var processor = env.Processor();
        var delays = new List<double>();
        ProcessOutcome last = ProcessOutcome.Retry;
        for (var i = 0; i < 10; i++)
        {
            var item = (await env.Inbox.ClaimNextAsync(default)) ?? await env.EnqueueAndClaim();
            var before = env.Now;
            last = await processor.ProcessAsync(item, default);
            if (last != ProcessOutcome.Retry) break;
            var next = UtcText.Parse((await env.InboxRows()).Single().NextAttemptUtc);
            delays.Add((next - before).TotalMinutes);
            env.Advance(next - before);
        }
        Assert.Equal(new double[] { 1, 5, 15, 60, 60, 60, 60, 60, 60 }, delays);
        Assert.Equal(ProcessOutcome.Failed, last);
    }

    [Fact]
    public async Task TransientError_ExhaustedAttempts_Fails()
    {
        using var env = new Env();
        env.FakeGraph.Throws(new GraphException("503", 503, true));
        var item = (await env.EnqueueAndClaim()) with { Attempts = env.Processing.MaxAttempts };
        Assert.Equal(ProcessOutcome.Failed, await env.Processor().ProcessAsync(item, default));
        var err = Assert.Single(await env.ErrorRows());
        Assert.Equal("graph request failed", err.Message);
        Assert.Contains("status=503", err.Details);
        Assert.Contains("attempts=10", err.Details);
    }

    [Fact]
    public async Task PermanentGraphError_FailsImmediately()
    {
        using var env = new Env();
        env.FakeGraph.Throws(new GraphException("403", 403, false));
        var item = await env.EnqueueAndClaim();
        Assert.Equal(ProcessOutcome.Failed, await env.Processor().ProcessAsync(item, default));
        var err = Assert.Single(await env.ErrorRows());
        Assert.Contains("status=403", err.Details);
        Assert.Contains("attempts=1", err.Details);
        Assert.Equal("failed", (await env.InboxRows()).Single().State);
    }

    [Fact]
    public async Task UnexpectedException_Reschedules()
    {
        using var env = new Env();
        env.FakeGraph.Throws(new InvalidOperationException("boom"));
        var item = await env.EnqueueAndClaim();
        Assert.Equal(ProcessOutcome.Retry, await env.Processor().ProcessAsync(item, default));
        Assert.Equal("unexpected processing error", (await env.ErrorRows()).Single().Message);
        Assert.Equal("new", (await env.InboxRows()).Single().State);
    }

    [Fact]
    public async Task Cancellation_PropagatesAndKeepsProcessing()
    {
        using var env = new Env();
        using var cts = new CancellationTokenSource();
        env.FakeGraph.OnGetCallRecord = (_, token) =>
        {
            cts.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult(new GraphFetchResult(GraphFetchStatus.NotFound, null));
        };
        var item = await env.EnqueueAndClaim();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => env.Processor().ProcessAsync(item, cts.Token));
        Assert.Equal("processing", (await env.InboxRows()).Single().State);
    }

    [Fact]
    public async Task DecisionChangedRepeatedly_ThreeTimes_Retries()
    {
        using var env = new Env();
        var applier = new FakeCdrApplier
        {
            OnApply = _ => new ApplyResult(ApplyStatus.DecisionChanged, VersionAction.IgnoreLower, 0, 0, 0, 0),
        };
        Assert.Equal(ProcessOutcome.Retry, await Run(env, GraphSamples.Simple(), applier: applier));
        Assert.Equal(3, applier.Calls);
        Assert.Contains("decision changed repeatedly", (await env.InboxRows()).Single().LastError);
    }

    [Fact]
    public async Task SessionLevelRejectionsNotWrittenWhileTemporaryReasonsPresent()
    {
        using var env = new Env();
        var record = GraphSamples.Record(
            GraphSamples.Session("s1", end: null) + "," + GraphSamples.Session("s2", "2026-10-20T17:55:00Z", "2026-10-20T17:50:00Z"));
        Assert.Equal(ProcessOutcome.Pending, await Run(env, record));
        Assert.Empty(await env.RejectedRows());
    }

    [Fact]
    public async Task SuspendedRecheck_ExpiresAfterRetention()
    {
        using var env = new Env(TestDb.T0.AddDays(30).AddMinutes(1));
        await env.SeedState("call-1", "suspended", attempts: 8, firstSeen: TestDb.T0, next: TestDb.T0.AddDays(30));
        env.FakeGraph.Returns(GraphFetchStatus.NotFound);
        var item = await env.EnqueueAndClaim(kind: InboxKinds.Reconcile);
        Assert.Equal(ProcessOutcome.Expired, await env.Processor().ProcessAsync(item, default));
        Assert.Equal("source_state expired", (await env.ErrorRows()).Single().Message);
    }

    [Fact]
    public async Task ReprocessSameElement_IsIdempotent()
    {
        using var env = new Env();
        env.FakeGraph.Returns(GraphFetchStatus.Ok, GraphSamples.Simple());
        var item = await env.EnqueueAndClaim();
        var processor = env.Processor();
        Assert.Equal(ProcessOutcome.Applied, await processor.ProcessAsync(item, default));
        // lease lost: the same item is processed again
        Assert.Equal(ProcessOutcome.Duplicate, await processor.ProcessAsync(item, default));
        Assert.Single(await env.CdrRows());
        Assert.Equal(2, (await env.OutboxRows()).Count);
    }

    [Fact]
    public async Task ReconcileWithRange_UsesRawPeriod()
    {
        using var env = new Env();
        var raw = "{\"from_utc\":\"2026-10-19T00:00:00Z\",\"to_utc\":\"2026-10-20T00:00:00Z\",\"force\":true}";
        var item = await env.EnqueueAndClaim(null, InboxKinds.Reconcile, raw);
        Assert.Equal(ProcessOutcome.ReconcileRun, await env.Processor().ProcessAsync(item, default));
        var call = Assert.Single(env.FakeReconciler.RangeCalls);
        Assert.Equal(new DateTimeOffset(2026, 10, 19, 0, 0, 0, TimeSpan.Zero), call.From);
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero), call.To);
        Assert.True(call.Force);
        Assert.Equal(0, env.FakeReconciler.LookbackCalls);
        Assert.Equal("done", (await env.InboxRows()).Single().State);
    }

    [Fact]
    public async Task ReconcileWithoutRange_RunsLookback()
    {
        using var env = new Env();
        var item = await env.EnqueueAndClaim(null, InboxKinds.Reconcile, "{\"scheduled\":true}");
        Assert.Equal(ProcessOutcome.ReconcileRun, await env.Processor().ProcessAsync(item, default));
        Assert.Equal(1, env.FakeReconciler.LookbackCalls);
        Assert.Empty(env.FakeReconciler.RangeCalls);
    }

    [Theory]
    [InlineData("{\"from_utc\":\"2026-10-19T00:00:00Z\",\"force\":true}")]
    [InlineData("{\"from_utc\":\"2026-10-01T00:00:00Z\",\"to_utc\":\"2026-10-20T00:00:00Z\",\"force\":true}")]
    [InlineData("{\"from_utc\":\"2026-10-19T00:00:00Z\",\"to_utc\":\"2026-10-20T00:00:00Z\",\"force\":\"yes\"}")]
    public async Task ReconcileBadRange_FailsNoLookback(string raw)
    {
        using var env = new Env();
        var item = await env.EnqueueAndClaim(null, InboxKinds.Reconcile, raw);
        Assert.Equal(ProcessOutcome.Failed, await env.Processor().ProcessAsync(item, default));
        var row = (await env.InboxRows()).Single();
        Assert.Equal("failed", row.State);
        Assert.Equal("RECONCILE_BAD_RANGE", row.LastError);
        Assert.Empty(env.FakeReconciler.RangeCalls);
        Assert.Equal(0, env.FakeReconciler.LookbackCalls);
    }
}
