using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TeamsCallRecords.Core;
using TeamsCallRecords.Infrastructure.Config;
using TeamsCallRecords.Infrastructure.Data;
using TeamsCallRecords.Infrastructure.Data.Entities;

namespace TeamsCallRecords.Infrastructure.Queue;

/// <summary>Incoming queue on top of the inbox table (TRP 3.12.3).</summary>
public sealed class InboxStore : IInboxStore
{
    private readonly IWriteGate _gate;
    private readonly IDbContextFactory<TcriDbContext> _factory;
    private readonly IOptions<InboxOptions> _inbox;
    private readonly IOptions<ProcessingOptions> _processing;
    private readonly TimeProvider _clock;

    public InboxStore(IWriteGate gate, IDbContextFactory<TcriDbContext> factory, IOptions<InboxOptions> inbox,
        IOptions<ProcessingOptions> processing, TimeProvider clock)
    {
        _gate = gate;
        _factory = factory;
        _inbox = inbox;
        _processing = processing;
        _clock = clock;
    }

    public static string ElementHash(string kind, string tenantId, string raw)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(raw); }
        catch (JsonException e) { throw new ArgumentException("raw is not valid JSON", nameof(raw), e); }
        using (doc)
            return Canonical.Sha256Hex(kind + "\n" + tenantId + "\n" + Canonical.HashOf(doc.RootElement));
    }

    public Task<InboxEnqueueResult> EnqueueAsync(IReadOnlyList<InboxNewItem> items, CancellationToken ct)
    {
        if (items.Count == 0) return Task.FromResult(new InboxEnqueueResult(0, 0, false));
        var hashes = items.Select(i => ElementHash(i.Kind, i.TenantId, i.Raw)).ToList();

        return _gate.RunAsync(async (db, token) =>
        {
            var pending = await db.InboxItems.CountAsync(
                x => x.State == InboxStates.New || x.State == InboxStates.Processing, token);
            if (pending >= _inbox.Value.MaxPending) return new InboxEnqueueResult(0, 0, true);

            var now = UtcText.Now(_clock);
            var seen = new HashSet<string>();
            int added = 0, duplicates = 0;
            for (var i = 0; i < items.Count; i++)
            {
                var hash = hashes[i];
                var exists = !seen.Add(hash) ||
                             await db.InboxItems.AnyAsync(x => x.ElementHash == hash && x.State == InboxStates.New, token);
                if (exists) { duplicates++; continue; }
                var item = items[i];
                db.InboxItems.Add(new InboxRow
                {
                    ElementHash = hash,
                    Kind = item.Kind,
                    TenantId = item.TenantId,
                    CallId = item.CallId,
                    Raw = item.Raw,
                    State = InboxStates.New,
                    Attempts = 0,
                    NextAttemptUtc = now,
                    ReceivedUtc = now,
                });
                added++;
            }
            await db.SaveChangesAsync(token);
            return new InboxEnqueueResult(added, duplicates, false);
        }, ct);
    }

    public Task<InboxItem?> ClaimNextAsync(CancellationToken ct) =>
        _gate.RunAsync<InboxItem?>(async (db, token) =>
        {
            var nowTime = _clock.GetUtcNow();
            var now = UtcText.Format(nowTime);
            var row = await db.InboxItems.FromSqlInterpolated($@"
SELECT * FROM inbox
WHERE next_attempt_utc <= {now}
  AND (state = 'new' OR (state = 'processing' AND locked_until_utc < {now}))
  AND (call_id IS NULL OR NOT EXISTS (
        SELECT 1 FROM inbox p
        WHERE p.call_id = inbox.call_id AND p.tenant_id = inbox.tenant_id AND p.id <> inbox.id
          AND p.state = 'processing' AND p.locked_until_utc >= {now}))")
                .OrderBy(x => x.NextAttemptUtc).ThenBy(x => x.Id).Take(1)
                .FirstOrDefaultAsync(token);
            if (row == null) return null;

            row.State = InboxStates.Processing;
            row.LockedUntilUtc = UtcText.Format(nowTime.AddSeconds(_processing.Value.LeaseSeconds));
            row.Attempts += 1;
            await db.SaveChangesAsync(token);
            return new InboxItem(row.Id, row.Kind, row.TenantId, row.CallId, row.Raw, row.Attempts,
                UtcText.Parse(row.ReceivedUtc), UtcText.Parse(row.LockedUntilUtc!));
        }, ct);

    public Task<DateTimeOffset?> RenewLeaseAsync(long id, DateTimeOffset expectedLockedUntilUtc, CancellationToken ct) =>
        _gate.RunAsync<DateTimeOffset?>(async (db, token) =>
        {
            var nowTime = _clock.GetUtcNow();
            var now = UtcText.Format(nowTime);
            var next = nowTime.AddSeconds(_processing.Value.LeaseSeconds);
            var nextText = UtcText.Format(next);
            var expected = UtcText.Format(expectedLockedUntilUtc);
            var n = await db.Database.ExecuteSqlInterpolatedAsync($@"
UPDATE inbox SET locked_until_utc = {nextText}
WHERE id = {id} AND state = 'processing' AND locked_until_utc = {expected} AND locked_until_utc > {now}", token);
            return n == 0 ? null : next;
        }, ct);

    public Task CompleteAsync(long id, CancellationToken ct) =>
        _gate.RunAsync(async (db, token) =>
        {
            InboxTransitions.MarkDone(db, id, UtcText.Now(_clock));
            await db.SaveChangesAsync(token);
        }, ct);

    public Task RescheduleAsync(long id, DateTimeOffset nextAttemptUtc, string? error, bool resetAttempts, CancellationToken ct) =>
        _gate.RunAsync(async (db, token) =>
        {
            InboxTransitions.Reschedule(db, id, UtcText.Now(_clock), UtcText.Format(nextAttemptUtc), error, resetAttempts);
            await db.SaveChangesAsync(token);
        }, ct);

    public Task FailAsync(long id, string error, CancellationToken ct) =>
        _gate.RunAsync(async (db, token) =>
        {
            InboxTransitions.MarkFailed(db, id, UtcText.Now(_clock), error);
            await db.SaveChangesAsync(token);
        }, ct);

    public async Task<InboxStats> GetStatsAsync(CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var pendingRows = db.InboxItems.AsNoTracking()
            .Where(x => x.State == InboxStates.New || x.State == InboxStates.Processing);
        var pending = await pendingRows.CountAsync(ct);
        var oldest = pending == 0 ? null : await pendingRows.MinAsync(x => x.ReceivedUtc, ct);
        var failed = await db.InboxItems.AsNoTracking().CountAsync(x => x.State == InboxStates.Failed, ct);
        long age = 0;
        if (oldest != null)
            age = Math.Max(0, (long)(_clock.GetUtcNow() - UtcText.Parse(oldest)).TotalSeconds);
        return new InboxStats(pending, age, failed);
    }
}
