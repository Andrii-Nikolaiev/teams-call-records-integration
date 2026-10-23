using Microsoft.EntityFrameworkCore;
using TeamsCallRecords.Infrastructure.Config;
using TeamsCallRecords.Infrastructure.Data;
using TeamsCallRecords.Infrastructure.Data.Entities;
using TeamsCallRecords.Infrastructure.Outbox;
using TeamsCallRecords.Infrastructure.Tests.Support;

namespace TeamsCallRecords.Infrastructure.Tests;

public class OutboxStoreTests
{
    private static readonly DateTimeOffset T0 = TestDb.T0;

    [Fact]
    public async Task ClaimNext_ReturnsDueNew()
    {
        using var env = new Env();
        var id = await env.SeedOutbox(next: T0);
        var item = await env.OutboxQ.ClaimNextAsync(default);
        Assert.Equal(id, item!.Id);
        Assert.Equal(1, item.Attempts);
        var row = (await env.OutboxRows()).Single();
        Assert.Equal("sending", row.State);
        Assert.Equal(UtcText.Format(T0.AddSeconds(120)), row.LockedUntilUtc);
    }

    [Fact]
    public async Task ClaimNext_SkipsFutureAndSentAndFailed()
    {
        using var env = new Env();
        await env.SeedOutbox(next: T0.AddMinutes(1), session: "a");
        await env.SeedOutbox(state: "sent", session: "b");
        await env.SeedOutbox(state: "failed", session: "c");
        Assert.Null(await env.OutboxQ.ClaimNextAsync(default));
    }

    [Fact]
    public async Task ClaimNext_ExpiredSending_BecomesFailedUnknown()
    {
        using var env = new Env();
        var stuck = await env.SeedOutbox(state: "sending", attempts: 1, locked: T0.AddSeconds(-1), session: "a");
        var next = await env.SeedOutbox(session: "b");

        var item = await env.OutboxQ.ClaimNextAsync(default);

        Assert.Equal(next, item!.Id);
        var row = (await env.OutboxRows()).Single(r => r.Id == stuck);
        Assert.Equal("failed", row.State);
        Assert.Equal("OUTCOME_UNKNOWN: lease expired", row.LastError);
        Assert.Null(row.LockedUntilUtc);
    }

    [Fact]
    public async Task ClaimNext_Parallel_NoDuplicates()
    {
        using var env = new Env();
        for (var i = 0; i < 3; i++) await env.SeedOutbox(session: "s" + i);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => env.OutboxQ.ClaimNextAsync(default)));
        var ids = results.Where(r => r != null).Select(r => r!.Id).ToList();
        Assert.Equal(3, ids.Count);
        Assert.Equal(3, ids.Distinct().Count());
    }

    [Fact]
    public async Task MarkSent_SetsSentUtc()
    {
        using var env = new Env();
        await env.SeedOutbox();
        var item = await env.OutboxQ.ClaimNextAsync(default);
        await env.OutboxQ.MarkSentAsync(item!.Id, default);
        var row = (await env.OutboxRows()).Single();
        Assert.Equal("sent", row.State);
        Assert.Equal(UtcText.Format(T0), row.SentUtc);
        Assert.Null(row.LastError);
        Assert.Null(row.LockedUntilUtc);
    }

    [Fact]
    public async Task Reschedule_BackoffTable()
    {
        using var env = new Env();
        var expected = new[] { 30, 120, 600, 1800, 3600, 3600, 3600 };
        for (var attempts = 1; attempts <= 7; attempts++)
        {
            var id = await env.SeedOutbox(state: "sending", attempts: attempts, locked: T0.AddSeconds(100), session: "s" + attempts);
            Assert.True(await env.OutboxQ.RescheduleAsync(id, "HTTP 503", default));
            var row = (await env.OutboxRows()).Single(r => r.Id == id);
            Assert.Equal("new", row.State);
            Assert.Equal(UtcText.Format(T0.AddSeconds(expected[attempts - 1])), row.NextAttemptUtc);
        }
    }

    [Fact]
    public async Task Reschedule_AtMaxAttempts_Fails()
    {
        using var env = new Env();
        var id = await env.SeedOutbox(state: "sending", attempts: 8, locked: T0.AddSeconds(100));
        Assert.False(await env.OutboxQ.RescheduleAsync(id, "HTTP 503", default));
        var row = (await env.OutboxRows()).Single();
        Assert.Equal("failed", row.State);
        Assert.Null(row.LockedUntilUtc);
    }

    [Fact]
    public async Task Fail_SetsFailedWithoutBackoff()
    {
        using var env = new Env();
        var id = await env.SeedOutbox(state: "sending", attempts: 1, locked: T0.AddSeconds(100));
        await env.OutboxQ.FailAsync(id, "forbidden", default);
        var row = (await env.OutboxRows()).Single();
        Assert.Equal("failed", row.State);
        Assert.Equal("forbidden", row.LastError);
    }

    [Fact]
    public async Task UniqueKey_PreventsSecondRowForSameRecipient()
    {
        using var env = new Env();
        await env.SeedOutbox();
        await Assert.ThrowsAsync<DbUpdateException>(() => env.SeedOutbox());
    }

    [Fact]
    public async Task GetStats_CountsByState()
    {
        using var env = new Env();
        await env.SeedOutbox(session: "a");
        await env.SeedOutbox(state: "sending", locked: T0.AddMinutes(1), session: "b");
        await env.SeedOutbox(state: "sent", session: "c");
        await env.SeedOutbox(state: "failed", session: "d");
        await env.SeedOutbox(state: "failed", session: "e");
        Assert.Equal(new OutboxStats(2, 2), await env.OutboxQ.GetStatsAsync(default));
    }

    [Theory]
    [InlineData(1, 30)]
    [InlineData(5, 3600)]
    [InlineData(6, 3600)]
    [InlineData(100, 3600)]
    public void BackoffFor_IndexIsClamped(int attempt, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), new OutboxOptions().BackoffFor(attempt));

    [Fact]
    public void BackoffFor_Zero_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new OutboxOptions().BackoffFor(0));
}
