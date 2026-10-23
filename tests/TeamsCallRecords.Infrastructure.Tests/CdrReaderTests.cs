using TeamsCallRecords.Infrastructure.Data;
using TeamsCallRecords.Infrastructure.Tests.Support;

namespace TeamsCallRecords.Infrastructure.Tests;

public class CdrReaderTests
{
    private static readonly DateTimeOffset Day = new(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);

    private static async Task Insert(TestDb db, params TeamsCallRecords.Core.Cdr[] cdrs)
    {
        await db.Gate.RunAsync(async (ctx, ct) =>
        {
            var now = UtcText.Now(db.Clock);
            foreach (var c in cdrs) ctx.Cdrs.Add(CdrMapping.ToRow(c, now));
            await ctx.SaveChangesAsync(ct);
        }, CancellationToken.None);
    }

    private static TeamsCallRecords.Core.Cdr At(string call, string session, int hour, int minute = 0, long dur = 60) =>
        Builders.Cdr(call, session, start: Day.AddHours(hour).AddMinutes(minute), durationSeconds: dur);

    [Fact]
    public async Task Query_ReturnsHalfOpenInterval_OrderedByStart()
    {
        using var db = TestDb.Create();
        await Insert(db, At("c3", "s", 12), At("c1", "s", 9), At("c2", "s", 10), At("c0", "s", 8), At("cEnd", "s", 24));
        var reader = new CdrReader(db.Factory);

        var page = await reader.QueryAsync(TestDb.Tenant, Day.AddHours(9), Day.AddHours(24), 100, null, default);

        Assert.Equal(new[] { "c1", "c2", "c3" }, page.Items.Select(i => i.Cdr.CallId));
        Assert.Null(page.Next);
    }

    [Fact]
    public async Task Query_Paging_NoGapsNoDuplicates()
    {
        using var db = TestDb.Create();
        // equal start_utc for two rows: the order is decided by cdr_seq
        await Insert(db, At("a", "1", 9), At("b", "1", 9), At("c", "1", 10), At("d", "1", 11), At("e", "1", 12));
        var reader = new CdrReader(db.Factory);

        var seen = new List<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await reader.QueryAsync(TestDb.Tenant, Day, Day.AddDays(1), 2, cursor, default);
            seen.AddRange(page.Items.Select(i => i.Cdr.CallId));
            cursor = page.Next;
            pages++;
        } while (cursor != null);

        Assert.Equal(new[] { "a", "b", "c", "d", "e" }, seen);
        Assert.Equal(3, pages);
    }

    [Fact]
    public async Task Query_InsertWhilePaging_LaterRowIsSeen()
    {
        using var db = TestDb.Create();
        await Insert(db, At("a", "1", 9), At("b", "1", 10), At("c", "1", 11));
        var reader = new CdrReader(db.Factory);

        var first = await reader.QueryAsync(TestDb.Tenant, Day, Day.AddDays(1), 2, null, default);
        await Insert(db, At("late", "1", 13));
        var second = await reader.QueryAsync(TestDb.Tenant, Day, Day.AddDays(1), 2, first.Next, default);

        Assert.Equal(new[] { "c", "late" }, second.Items.Select(i => i.Cdr.CallId));
    }

    [Fact]
    public async Task Query_OtherTenant_IsNotReturned()
    {
        using var db = TestDb.Create();
        await Insert(db, At("a", "1", 9));
        var reader = new CdrReader(db.Factory);
        var page = await reader.QueryAsync("22222222-2222-4222-8222-222222222222", Day, Day.AddDays(1), 10, null, default);
        Assert.Empty(page.Items);
    }

    [Fact]
    public async Task Query_Items_CarryCdrSeq()
    {
        using var db = TestDb.Create();
        await Insert(db, At("a", "1", 9), At("b", "1", 10));
        var page = await new CdrReader(db.Factory).QueryAsync(TestDb.Tenant, Day, Day.AddDays(1), 10, null, default);
        Assert.Equal(new long[] { 1, 2 }, page.Items.Select(i => i.CdrSeq));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1001)]
    public async Task Query_BadLimit_Throws(int limit)
    {
        using var db = TestDb.Create();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            new CdrReader(db.Factory).QueryAsync(TestDb.Tenant, Day, Day.AddDays(1), limit, null, default));
    }

    [Fact]
    public async Task Query_FromAfterTo_Throws()
    {
        using var db = TestDb.Create();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            new CdrReader(db.Factory).QueryAsync(TestDb.Tenant, Day.AddDays(1), Day, 10, null, default));
    }

    [Fact]
    public async Task Query_MalformedCursor_Throws()
    {
        using var db = TestDb.Create();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            new CdrReader(db.Factory).QueryAsync(TestDb.Tenant, Day, Day.AddDays(1), 10, "garbage!", default));
    }

    [Fact]
    public async Task Stats_CountsDistinctCallsDurationAndCdrs()
    {
        using var db = TestDb.Create();
        await Insert(db, At("a", "1", 9, dur: 60), At("a", "2", 9, 30, dur: 40), At("b", "1", 10, dur: 100), At("out", "1", 23, dur: 5000));
        var stats = await new CdrReader(db.Factory).GetStatsAsync(TestDb.Tenant, Day.AddHours(9), Day.AddHours(12), default);
        Assert.Equal(new DailyStats(2, 200, 3), stats);
    }

    [Fact]
    public async Task Stats_Empty_IsZero()
    {
        using var db = TestDb.Create();
        var stats = await new CdrReader(db.Factory).GetStatsAsync(TestDb.Tenant, Day, Day.AddDays(1), default);
        Assert.Equal(new DailyStats(0, 0, 0), stats);
    }
}
