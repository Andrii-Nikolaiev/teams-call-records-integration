using Microsoft.EntityFrameworkCore;
using TeamsCallRecords.Infrastructure.Data.Entities;
using TeamsCallRecords.Infrastructure.Tests.Support;

namespace TeamsCallRecords.Infrastructure.Tests;

public class WriteGateTests
{
    private static ErrorLogRow Row(string message) => new()
    {
        Ts = "2026-10-20T18:00:00.0000000Z",
        Component = Components.Startup,
        Message = message,
    };

    [Fact]
    public async Task Run_Commits()
    {
        using var db = TestDb.Create();
        await db.Gate.RunAsync(async (ctx, ct) =>
        {
            ctx.ErrorLog.Add(Row("committed"));
            await ctx.SaveChangesAsync(ct);
        }, CancellationToken.None);

        var rows = await db.ReadAsync(ctx => ctx.ErrorLog.AsNoTracking().ToListAsync());
        Assert.Single(rows);
        Assert.Equal("committed", rows[0].Message);
    }

    [Fact]
    public async Task Run_ReturnsValue()
    {
        using var db = TestDb.Create();
        var result = await db.Gate.RunAsync<int>((_, _) => Task.FromResult(42), CancellationToken.None);
        Assert.Equal(42, result);
    }

    [Fact]
    public async Task Run_RollsBackOnException()
    {
        using var db = TestDb.Create();
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Gate.RunAsync(async (ctx, ct) =>
        {
            ctx.ErrorLog.Add(Row("lost"));
            await ctx.SaveChangesAsync(ct);
            throw new InvalidOperationException("boom");
        }, CancellationToken.None));

        var count = await db.ReadAsync(ctx => ctx.ErrorLog.CountAsync());
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Run_ReleasesGateAfterException()
    {
        using var db = TestDb.Create();
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Gate.RunAsync(async (_, _) =>
        {
            await Task.Yield();
            throw new InvalidOperationException("boom");
        }, CancellationToken.None));

        var second = db.Gate.RunAsync<int>((_, _) => Task.FromResult(1), CancellationToken.None);
        var finished = await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(second, finished);
        Assert.Equal(1, await second);
    }

    [Fact]
    public async Task Run_ExecutesOneAtATime()
    {
        using var db = TestDb.Create();
        var inside = 0;
        var maxInside = 0;
        var done = 0;

        var tasks = Enumerable.Range(0, 20).Select(_ => db.Gate.RunAsync(async (_, _) =>
        {
            var now = Interlocked.Increment(ref inside);
            InterlockedMax(ref maxInside, now);
            await Task.Delay(5);
            Interlocked.Decrement(ref inside);
            Interlocked.Increment(ref done);
        }, CancellationToken.None)).ToArray();

        await Task.WhenAll(tasks);
        Assert.Equal(1, maxInside);
        Assert.Equal(20, done);
    }

    [Fact]
    public async Task Run_CancelledBeforeEntry()
    {
        using var db = TestDb.Create();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var called = false;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => db.Gate.RunAsync((_, _) =>
        {
            called = true;
            return Task.CompletedTask;
        }, cts.Token));

        Assert.False(called);
    }

    [Fact]
    public async Task Readers_DoNotWaitForGate()
    {
        using var db = TestDb.Create();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var writer = db.Gate.RunAsync(async (ctx, ct) =>
        {
            ctx.ErrorLog.Add(Row("held"));
            await ctx.SaveChangesAsync(ct);   // the write transaction is open now
            entered.SetResult();
            await release.Task;
        }, CancellationToken.None);

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var read = db.ReadAsync(ctx => ctx.ErrorLog.CountAsync());
        var finished = await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(read, finished);
        Assert.Equal(0, await read);   // the uncommitted row is not visible to readers

        release.SetResult();
        await writer;
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value &&
               Interlocked.CompareExchange(ref target, value, current) != current)
        { }
    }
}
