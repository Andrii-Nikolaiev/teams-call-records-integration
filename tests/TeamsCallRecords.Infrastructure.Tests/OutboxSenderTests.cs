using TeamsCallRecords.Infrastructure.Graph;
using TeamsCallRecords.Infrastructure.Tests.Support;

namespace TeamsCallRecords.Infrastructure.Tests;

public class OutboxSenderTests
{
    [Fact]
    public async Task RunOnce_NoItems_ReturnsFalse()
    {
        using var env = new Env();
        Assert.False(await env.Sender.RunOnceAsync(default));
        Assert.Empty(env.FakeGraph.Sent);
    }

    [Fact]
    public async Task RunOnce_Success_MarksSent()
    {
        using var env = new Env();
        await env.SeedOutbox(recipient: "user-a");

        Assert.True(await env.Sender.RunOnceAsync(default));

        Assert.Equal("sent", (await env.OutboxRows()).Single().State);
        var n = Assert.Single(env.FakeGraph.Sent);
        Assert.Equal("user-a", n.RecipientUserId);
        Assert.Equal("callCompleted", n.ActivityType);
        Assert.Equal("Teams call", n.TopicText);
        Assert.Equal("Call oneToOne, 61 s, completed", n.PreviewText);
        Assert.Equal(new[] { "callType", "status", "duration", "startUtc" }, n.TemplateParameters.Keys);
        Assert.Equal(new[] { "oneToOne", "completed", "61", "2026-10-20T09:00:00.0000000Z" }, n.TemplateParameters.Values);
    }

    [Fact]
    public async Task RunOnce_TransientError_Reschedules()
    {
        using var env = new Env();
        await env.SeedOutbox();
        env.FakeGraph.OnSend = (_, _) => throw new GraphException("429", 429, true);

        Assert.True(await env.Sender.RunOnceAsync(default));

        var row = (await env.OutboxRows()).Single();
        Assert.Equal("new", row.State);
        Assert.Equal(TestDb.T0.AddSeconds(30), TeamsCallRecords.Infrastructure.Data.UtcText.Parse(row.NextAttemptUtc));
        Assert.Empty(await env.ErrorRows());
    }

    [Fact]
    public async Task RunOnce_TransientError_EightAttempts_Fails()
    {
        using var env = new Env();
        await env.SeedOutbox();
        env.FakeGraph.OnSend = (_, _) => throw new GraphException("429", 429, true);

        for (var i = 0; i < 8; i++)
        {
            Assert.True(await env.Sender.RunOnceAsync(default));
            env.Advance(TimeSpan.FromHours(2));
        }

        var row = (await env.OutboxRows()).Single();
        Assert.Equal("failed", row.State);
        Assert.Equal(8, row.Attempts);
        Assert.Equal("outbox item failed", Assert.Single(await env.ErrorRows()).Message);
    }

    [Fact]
    public async Task RunOnce_PermanentError_FailsImmediately()
    {
        using var env = new Env();
        await env.SeedOutbox();
        env.FakeGraph.OnSend = (_, _) => throw new GraphException("403", 403, false);
        Assert.True(await env.Sender.RunOnceAsync(default));
        var row = (await env.OutboxRows()).Single();
        Assert.Equal("failed", row.State);
        Assert.Equal(1, row.Attempts);
        Assert.Single(await env.ErrorRows());
    }

    [Fact]
    public async Task RunOnce_OutcomeUnknown_FailsWithoutRetry()
    {
        using var env = new Env();
        await env.SeedOutbox();
        env.FakeGraph.OnSend = (_, _) => throw new GraphException("timeout", null, true, outcomeUnknown: true);

        Assert.True(await env.Sender.RunOnceAsync(default));

        var row = (await env.OutboxRows()).Single();
        Assert.Equal("failed", row.State);
        Assert.Equal(1, row.Attempts);
        Assert.Equal("OUTCOME_UNKNOWN: timeout", row.LastError);
        Assert.Contains("outcome=unknown", Assert.Single(await env.ErrorRows()).Details);
        Assert.False(await env.Sender.RunOnceAsync(default));
    }

    [Fact]
    public async Task RunOnce_UnexpectedException_TreatedAsOutcomeUnknown()
    {
        using var env = new Env();
        await env.SeedOutbox();
        env.FakeGraph.OnSend = (_, _) => throw new InvalidOperationException("boom");
        Assert.True(await env.Sender.RunOnceAsync(default));
        var row = (await env.OutboxRows()).Single();
        Assert.Equal("failed", row.State);
        Assert.Equal("OUTCOME_UNKNOWN: InvalidOperationException", row.LastError);
    }

    [Fact]
    public async Task RunOnce_BadPayload_Fails()
    {
        using var env = new Env();
        await env.SeedOutbox(payload: "{ bad");
        Assert.True(await env.Sender.RunOnceAsync(default));
        var row = (await env.OutboxRows()).Single();
        Assert.Equal("failed", row.State);
        Assert.Equal("bad payload", row.LastError);
        Assert.Single(await env.ErrorRows());
        Assert.Empty(env.FakeGraph.Sent);
    }

    [Fact]
    public async Task RunOnce_SendFailure_DoesNotTouchCdr()
    {
        using var env = new Env();
        var item = await env.EnqueueAndClaim();
        await env.Applier.ApplyAsync(new TeamsCallRecords.Infrastructure.Processing.ApplyRequest(
            TestDb.Tenant, TeamsCallRecords.Core.CallRecordConverter.Convert(GraphSamples.Simple(), TestDb.Tenant), item.Id), default);
        var cdrBefore = await env.CdrRows();
        var stateBefore = await env.StateRows();
        var inboxBefore = await env.InboxRows();
        env.FakeGraph.OnSend = (_, _) => throw new GraphException("403", 403, false);

        await env.Sender.RunOnceAsync(default);

        Assert.Equal(cdrBefore.Count, (await env.CdrRows()).Count);
        Assert.Equal(stateBefore.Single().AppliedVersion, (await env.StateRows()).Single().AppliedVersion);
        Assert.Equal(inboxBefore.Single().State, (await env.InboxRows()).Single().State);
    }

    [Fact]
    public async Task RunOnce_Cancellation_KeepsSending()
    {
        using var env = new Env();
        await env.SeedOutbox();
        using var cts = new CancellationTokenSource();
        env.FakeGraph.OnSend = (_, ct) => { cts.Cancel(); ct.ThrowIfCancellationRequested(); return Task.CompletedTask; };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => env.Sender.RunOnceAsync(cts.Token));

        Assert.Equal("sending", (await env.OutboxRows()).Single().State);
    }
}
