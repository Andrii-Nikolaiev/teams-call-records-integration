using Microsoft.EntityFrameworkCore;
using TeamsCallRecords.Infrastructure.Data.Entities;

namespace TeamsCallRecords.Infrastructure.Data;

/// <summary>Read-only CDR queries with keyset paging over [from, to) (TRP 3.11.7).</summary>
public sealed class CdrReader : ICdrReader
{
    private readonly IDbContextFactory<TcriDbContext> _factory;

    public CdrReader(IDbContextFactory<TcriDbContext> factory) => _factory = factory;

    public async Task<CdrPage> QueryAsync(string tenantId, DateTimeOffset fromUtc, DateTimeOffset toUtc,
        int limit, string? cursor, CancellationToken ct)
    {
        if (limit < 1 || limit > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        if (fromUtc > toUtc) throw new ArgumentException("fromUtc must be <= toUtc");
        var from = UtcText.Format(fromUtc);
        var to = UtcText.Format(toUtc);
        var take = limit + 1;

        await using var db = await _factory.CreateDbContextAsync(ct);
        List<CdrRow> rows;
        if (string.IsNullOrEmpty(cursor))
        {
            rows = await db.Cdrs.FromSqlInterpolated(
                    $"SELECT * FROM cdr WHERE tenant_id = {tenantId} AND start_utc >= {from} AND start_utc < {to}")
                .AsNoTracking().OrderBy(r => r.StartUtc).ThenBy(r => r.CdrSeq).Take(take).ToListAsync(ct);
        }
        else
        {
            var (cs, cq) = Cursor.Decode(cursor);
            rows = await db.Cdrs.FromSqlInterpolated(
                    $"SELECT * FROM cdr WHERE tenant_id = {tenantId} AND start_utc >= {from} AND start_utc < {to} AND (start_utc > {cs} OR (start_utc = {cs} AND cdr_seq > {cq}))")
                .AsNoTracking().OrderBy(r => r.StartUtc).ThenBy(r => r.CdrSeq).Take(take).ToListAsync(ct);
        }

        string? next = null;
        if (rows.Count > limit)
        {
            rows.RemoveAt(rows.Count - 1);
            var last = rows[^1];
            next = Cursor.Encode(last.StartUtc, last.CdrSeq);
        }
        var items = rows.Select(r => new CdrItem(r.CdrSeq, CdrMapping.ToCdr(r))).ToList();
        return new CdrPage(items, next);
    }

    public async Task<DailyStats> GetStatsAsync(string tenantId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        var from = UtcText.Format(fromUtc);
        var to = UtcText.Format(toUtc);
        await using var db = await _factory.CreateDbContextAsync(ct);
        var rows = await db.Cdrs.FromSqlInterpolated(
                $"SELECT * FROM cdr WHERE tenant_id = {tenantId} AND start_utc >= {from} AND start_utc < {to}")
            .AsNoTracking()
            .Select(r => new { r.CallId, r.DurationS })
            .ToListAsync(ct);
        return new DailyStats(rows.Select(r => r.CallId).Distinct().Count(), rows.Sum(r => r.DurationS), rows.Count);
    }
}
