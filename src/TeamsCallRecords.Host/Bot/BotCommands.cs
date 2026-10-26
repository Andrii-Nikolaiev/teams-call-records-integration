using Microsoft.Extensions.Options;
using TeamsCallRecords.Host.Config;
using TeamsCallRecords.Infrastructure.Config;
using TeamsCallRecords.Infrastructure.Data;

namespace TeamsCallRecords.Host.Bot;

/// <summary>
/// Text commands of the Teams bot (TRP 3.24). No Teams SDK types here: the class takes a text and returns a text,
/// so it is testable without the network. Commands are trimmed and case-insensitive; answers are in English.
/// </summary>
public sealed class BotCommands
{
    public const string HelpText = "Commands: help; calls today.";
    public const string UnknownText = "Unknown command. Type help.";

    private readonly ICdrReader _reader;
    private readonly string _tenantId;
    private readonly string _zoneLabel;

    public BotCommands(ICdrReader reader, IOptions<GraphOptions> graph, IOptions<BotOptions> bot)
    {
        _reader = reader;
        _tenantId = graph.Value.TenantId;
        _zoneLabel = bot.Value.TimeZoneId;   // shown to the user exactly as configured (Europe/Kyiv), whatever the system calls the zone
    }

    public async Task<string> ExecuteAsync(string text, DateTimeOffset nowUtc, TimeZoneInfo timeZone, CancellationToken ct)
    {
        var command = (text ?? "").Trim();
        if (command.Equals("help", StringComparison.OrdinalIgnoreCase)) return HelpText;
        if (command.Equals("calls today", StringComparison.OrdinalIgnoreCase)) return await CallsTodayAsync(nowUtc, timeZone, ct);
        return UnknownText;
    }

    private async Task<string> CallsTodayAsync(DateTimeOffset nowUtc, TimeZoneInfo timeZone, CancellationToken ct)
    {
        var today = TimeZoneInfo.ConvertTime(nowUtc, timeZone).Date;
        var fromUtc = LocalMidnightUtc(today, timeZone);
        var toUtc = LocalMidnightUtc(today.AddDays(1), timeZone);

        var stats = await _reader.GetStatsAsync(_tenantId, fromUtc, toUtc, ct);
        if (stats.Calls == 0) return $"Today ({_zoneLabel}): no calls recorded.";

        var duration = TimeSpan.FromSeconds(stats.TotalDurationSeconds);
        var hms = $"{(long)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";
        var noun = stats.Calls == 1 ? "call" : "calls";
        return $"Today ({_zoneLabel}): {stats.Calls} {noun}, total session duration {hms}.";
    }

    /// <summary>The first instant of the local date (a day can be 23 or 25 hours long around a DST change).</summary>
    private static DateTimeOffset LocalMidnightUtc(DateTime localDate, TimeZoneInfo timeZone)
    {
        var local = DateTime.SpecifyKind(localDate.Date, DateTimeKind.Unspecified);
        if (timeZone.IsInvalidTime(local)) local = local.AddHours(1);          // midnight skipped by a DST gap
        var offset = timeZone.IsAmbiguousTime(local)
            ? timeZone.GetAmbiguousTimeOffsets(local).Max()                     // the earlier of the two instants
            : timeZone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }
}
