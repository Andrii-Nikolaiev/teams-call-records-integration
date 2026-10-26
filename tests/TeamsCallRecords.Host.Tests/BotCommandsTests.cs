using Microsoft.Extensions.Options;
using TeamsCallRecords.Host.Bot;
using TeamsCallRecords.Host.Config;
using TeamsCallRecords.Infrastructure.Config;
using TeamsCallRecords.Infrastructure.Data;

namespace TeamsCallRecords.Host.Tests;

public sealed class BotCommandsTests
{
    private const string Tenant = "11111111-1111-4111-8111-111111111111";
    private static readonly TimeZoneInfo Kyiv = BotOptions.FindTimeZone("Europe/Kyiv");

    private sealed record Row(string TenantId, DateTimeOffset StartUtc, string CallId, long DurationS);

    /// <summary>Same selection rules as CdrReader.GetStatsAsync: tenant, start >= from, start &lt; to; calls are distinct call ids.</summary>
    private sealed class FakeReader : ICdrReader
    {
        public List<Row> Rows { get; } = new();
        public DateTimeOffset LastFromUtc { get; private set; }
        public DateTimeOffset LastToUtc { get; private set; }

        public Task<DailyStats> GetStatsAsync(string tenantId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
        {
            LastFromUtc = fromUtc;
            LastToUtc = toUtc;
            var hit = Rows.Where(r => r.TenantId == tenantId && r.StartUtc >= fromUtc && r.StartUtc < toUtc).ToList();
            return Task.FromResult(new DailyStats(hit.Select(r => r.CallId).Distinct().Count(), hit.Sum(r => r.DurationS), hit.Count));
        }

        public Task<CdrPage> QueryAsync(string tenantId, DateTimeOffset fromUtc, DateTimeOffset toUtc, int limit, string? cursor, CancellationToken ct)
            => throw new NotSupportedException();
    }

    private static (BotCommands Commands, FakeReader Reader) Create()
    {
        var reader = new FakeReader();
        var graph = Options.Create(new GraphOptions { TenantId = Tenant });
        return (new BotCommands(reader, graph, Options.Create(new BotOptions())), reader);
    }

    private static DateTimeOffset Utc(string iso) => DateTimeOffset.Parse(iso, null, System.Globalization.DateTimeStyles.AssumeUniversal);

    [Fact]
    public async Task HelpListsCommands()
    {
        var (bot, _) = Create();
        Assert.Equal("Commands: help; calls today.", await bot.ExecuteAsync("help", Utc("2026-10-20T12:00:00Z"), Kyiv, default));
    }

    [Fact]
    public async Task CallsTodayEmpty()
    {
        var (bot, _) = Create();
        Assert.Equal("Today (Europe/Kyiv): no calls recorded.", await bot.ExecuteAsync("calls today", Utc("2026-10-20T12:00:00Z"), Kyiv, default));
    }

    [Fact]
    public async Task CallsTodayThreeCalls()
    {
        var (bot, reader) = Create();
        reader.Rows.Add(new Row(Tenant, Utc("2026-10-20T06:00:00Z"), "call-1", 300));
        reader.Rows.Add(new Row(Tenant, Utc("2026-10-20T08:00:00Z"), "call-2", 400));
        reader.Rows.Add(new Row(Tenant, Utc("2026-10-20T09:00:00Z"), "call-3", 50));
        reader.Rows.Add(new Row("22222222-2222-4222-8222-222222222222", Utc("2026-10-20T09:00:00Z"), "other-tenant", 999));
        Assert.Equal("Today (Europe/Kyiv): 3 calls, total session duration 00:12:30.",
            await bot.ExecuteAsync("calls today", Utc("2026-10-20T12:00:00Z"), Kyiv, default));
    }

    [Fact]
    public async Task CallsTodayTimezoneBoundary()
    {
        // 2026-10-20 15:00 in Kyiv (UTC+3): the local day is 19 Oct 21:00Z .. 20 Oct 21:00Z.
        var (bot, reader) = Create();
        reader.Rows.Add(new Row(Tenant, Utc("2026-10-19T20:59:59Z"), "yesterday", 60));
        reader.Rows.Add(new Row(Tenant, Utc("2026-10-19T21:00:00Z"), "first-instant", 60));
        reader.Rows.Add(new Row(Tenant, Utc("2026-10-20T20:59:59Z"), "last-instant", 60));
        reader.Rows.Add(new Row(Tenant, Utc("2026-10-20T21:00:00Z"), "tomorrow", 60));

        var answer = await bot.ExecuteAsync("calls today", Utc("2026-10-20T12:00:00Z"), Kyiv, default);

        Assert.Equal("Today (Europe/Kyiv): 2 calls, total session duration 00:02:00.", answer);
        Assert.Equal(Utc("2026-10-19T21:00:00Z"), reader.LastFromUtc);
        Assert.Equal(Utc("2026-10-20T21:00:00Z"), reader.LastToUtc);
    }

    [Fact]
    public async Task CallsTodayDSTBoundary()
    {
        // 2026-10-25: clocks go back in Kyiv at 04:00 local (01:00Z), so the local day is 25 hours long:
        // 24 Oct 21:00Z (UTC+3) .. 25 Oct 22:00Z (UTC+2).
        var (bot, reader) = Create();
        reader.Rows.Add(new Row(Tenant, Utc("2026-10-24T20:59:59Z"), "yesterday", 60));
        reader.Rows.Add(new Row(Tenant, Utc("2026-10-24T21:00:00Z"), "first-instant", 60));
        reader.Rows.Add(new Row(Tenant, Utc("2026-10-25T21:59:59Z"), "last-instant", 60));
        reader.Rows.Add(new Row(Tenant, Utc("2026-10-25T22:00:00Z"), "tomorrow", 60));

        var answer = await bot.ExecuteAsync("calls today", Utc("2026-10-25T12:00:00Z"), Kyiv, default);

        Assert.Equal("Today (Europe/Kyiv): 2 calls, total session duration 00:02:00.", answer);
        Assert.Equal(TimeSpan.FromHours(25), reader.LastToUtc - reader.LastFromUtc);
    }

    [Theory]
    [InlineData("dance")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("calls yesterday")]
    public async Task UnknownCommand(string text)
    {
        var (bot, _) = Create();
        Assert.Equal("Unknown command. Type help.", await bot.ExecuteAsync(text, Utc("2026-10-20T12:00:00Z"), Kyiv, default));
    }

    [Theory]
    [InlineData("HELP", "Commands: help; calls today.")]
    [InlineData("  Help  ", "Commands: help; calls today.")]
    [InlineData("Calls Today", "Today (Europe/Kyiv): no calls recorded.")]
    [InlineData(" CALLS TODAY ", "Today (Europe/Kyiv): no calls recorded.")]
    public async Task CaseInsensitive(string text, string expected)
    {
        var (bot, _) = Create();
        Assert.Equal(expected, await bot.ExecuteAsync(text, Utc("2026-10-20T12:00:00Z"), Kyiv, default));
    }

    [Fact]
    public async Task DurationIsSessionSum()
    {
        // One call with two sessions: one call, the durations are summed.
        var (bot, reader) = Create();
        reader.Rows.Add(new Row(Tenant, Utc("2026-10-20T06:00:00Z"), "call-1", 600));
        reader.Rows.Add(new Row(Tenant, Utc("2026-10-20T06:00:00Z"), "call-1", 600));
        Assert.Equal("Today (Europe/Kyiv): 1 call, total session duration 00:20:00.",
            await bot.ExecuteAsync("calls today", Utc("2026-10-20T12:00:00Z"), Kyiv, default));
    }
}
