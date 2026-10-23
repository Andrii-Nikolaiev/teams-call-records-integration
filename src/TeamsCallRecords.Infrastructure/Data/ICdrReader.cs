using TeamsCallRecords.Core;

namespace TeamsCallRecords.Infrastructure.Data;

public sealed record CdrItem(long CdrSeq, Cdr Cdr);
public sealed record CdrPage(IReadOnlyList<CdrItem> Items, string? Next);
public sealed record DailyStats(int Calls, long TotalDurationSeconds, int Cdrs);

public interface ICdrReader
{
    Task<CdrPage> QueryAsync(string tenantId, DateTimeOffset fromUtc, DateTimeOffset toUtc, int limit, string? cursor, CancellationToken ct);
    Task<DailyStats> GetStatsAsync(string tenantId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct);
}
