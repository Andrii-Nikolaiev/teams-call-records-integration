using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TeamsCallRecords.Core;
using TeamsCallRecords.Infrastructure.Config;
using TeamsCallRecords.Infrastructure.Data;
using TeamsCallRecords.Infrastructure.Data.Entities;
using TeamsCallRecords.Infrastructure.Diagnostics;
using TeamsCallRecords.Infrastructure.Queue;

namespace TeamsCallRecords.Infrastructure.Processing;

/// <summary>Per-call state: version bookkeeping, incomplete-data episodes, rejections (TRP 3.13.4).</summary>
public sealed class SourceStateStore : ISourceStateStore
{
    private readonly IWriteGate _gate;
    private readonly IDbContextFactory<TcriDbContext> _factory;
    private readonly IOptions<ProcessingOptions> _processing;
    private readonly IOptions<GraphOptions> _graph;
    private readonly TimeProvider _clock;

    public SourceStateStore(IWriteGate gate, IDbContextFactory<TcriDbContext> factory, IOptions<ProcessingOptions> processing,
        IOptions<GraphOptions> graph, TimeProvider clock)
    {
        _gate = gate;
        _factory = factory;
        _processing = processing;
        _graph = graph;
        _clock = clock;
    }

    public async Task<SourceStateRow?> GetAsync(string tenantId, string callId, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.SourceStates.AsNoTracking().FirstOrDefaultAsync(r => r.TenantId == tenantId && r.CallId == callId, ct);
    }

    public async Task<IReadOnlyDictionary<string, SourceStateRow>> GetManyAsync(string tenantId, IReadOnlyCollection<string> callIds, CancellationToken ct)
    {
        var result = new Dictionary<string, SourceStateRow>();
        await using var db = await _factory.CreateDbContextAsync(ct);
        foreach (var chunk in callIds.Distinct().Chunk(500))
        {
            var ids = chunk.ToList();
            var rows = await db.SourceStates.AsNoTracking()
                .Where(r => r.TenantId == tenantId && ids.Contains(r.CallId)).ToListAsync(ct);
            foreach (var r in rows) result[r.CallId] = r;
        }
        return result;
    }

    public async Task<SourceStateCounts> GetCountsAsync(CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var groups = await db.SourceStates.AsNoTracking().GroupBy(r => r.State)
            .Select(g => new { State = g.Key, Count = g.Count() }).ToListAsync(ct);
        int Of(string s) => groups.FirstOrDefault(g => g.State == s)?.Count ?? 0;
        return new SourceStateCounts(Of(SourceStates.Pending), Of(SourceStates.Suspended), Of(SourceStates.Expired));
    }

    public async Task<IReadOnlyList<SourceStateRow>> ListSuspendedDueAsync(int limit, CancellationToken ct)
    {
        var now = UtcText.Now(_clock);
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.SourceStates.FromSqlInterpolated(
                $"SELECT * FROM source_state WHERE state = 'suspended' AND next_attempt_utc <= {now}")
            .AsNoTracking().OrderBy(r => r.NextAttemptUtc).Take(limit).ToListAsync(ct);
    }

    public Task<PendingResult> RegisterIncompleteAsync(string tenantId, string callId, string reason, long inboxId, CancellationToken ct) =>
        _gate.RunAsync(async (db, token) =>
        {
            var nowTime = _clock.GetUtcNow();
            var now = UtcText.Format(nowTime);
            var opt = _processing.Value;

            var row = await db.SourceStates.FindAsync(new object[] { tenantId, callId }, token);
            if (row == null)
            {
                row = new SourceStateRow { TenantId = tenantId, CallId = callId };
                db.SourceStates.Add(row);
                StartEpisode(row, now);
            }
            else if (row.State == SourceStates.Done)
            {
                StartEpisode(row, now);
            }
            else
            {
                row.Attempts += 1;
            }
            row.LastError = reason;
            row.UpdatedUtc = now;

            var age = nowTime - UtcText.Parse(row.FirstSeenUtc);
            PendingResult result;
            switch (row.State)
            {
                case SourceStates.Pending:
                    if (age >= TimeSpan.FromHours(opt.PendingLimitHours))
                    {
                        var recheck = nowTime.AddHours(opt.SuspendedRecheckHours);
                        row.State = SourceStates.Suspended;
                        row.NextAttemptUtc = UtcText.Format(recheck);
                        InboxTransitions.MarkDone(db, inboxId, now);
                        result = new PendingResult(SourceStates.Suspended, recheck);
                    }
                    else
                    {
                        var next = nowTime + opt.RetryDelayFor(row.Attempts);
                        row.NextAttemptUtc = UtcText.Format(next);
                        InboxTransitions.Reschedule(db, inboxId, now, row.NextAttemptUtc, reason, resetAttempts: false);
                        result = new PendingResult(SourceStates.Pending, next);
                    }
                    break;

                case SourceStates.Suspended:
                    if (age > TimeSpan.FromDays(_graph.Value.RetentionDays))
                    {
                        row.State = SourceStates.Expired;
                        row.NextAttemptUtc = null;
                        db.ErrorLog.Add(new ErrorLogRow
                        {
                            Ts = now,
                            Component = Components.Processor,
                            Message = "source_state expired",
                            Details = $"tenant_id={tenantId}; call_id={callId}",
                        });
                        InboxTransitions.MarkDone(db, inboxId, now);
                        result = new PendingResult(SourceStates.Expired, null);
                    }
                    else
                    {
                        var recheck = nowTime.AddHours(opt.SuspendedRecheckHours);
                        row.NextAttemptUtc = UtcText.Format(recheck);
                        InboxTransitions.MarkDone(db, inboxId, now);
                        result = new PendingResult(SourceStates.Suspended, recheck);
                    }
                    break;

                default: // expired: nothing changes
                    InboxTransitions.MarkDone(db, inboxId, now);
                    result = new PendingResult(SourceStates.Expired, null);
                    break;
            }

            await db.SaveChangesAsync(token);
            return result;
        }, ct);

    private static void StartEpisode(SourceStateRow row, string now)
    {
        row.State = SourceStates.Pending;
        row.FirstSeenUtc = now;
        row.Attempts = 1;
    }

    public Task RecordRejectionAsync(string tenantId, string? callId, long? version, IReadOnlyList<Rejection> rejections,
        string sourceHash, long inboxId, CancellationToken ct) =>
        _gate.RunAsync(async (db, token) =>
        {
            var now = UtcText.Now(_clock);

            foreach (var r in rejections)
            {
                var rCall = r.CallId ?? callId;
                var rSession = r.SessionId;
                var exists = db.RejectedItems.Local.Any(x => x.TenantId == tenantId && x.CallId == rCall && x.SessionId == rSession
                                                              && x.Code == r.Code && x.SourceHash == sourceHash)
                             || await db.RejectedItems.AnyAsync(x => x.TenantId == tenantId && x.CallId == rCall && x.SessionId == rSession
                                                                     && x.Code == r.Code && x.SourceHash == sourceHash, token);
                if (exists) continue;
                db.RejectedItems.Add(new RejectedRow
                {
                    TenantId = tenantId,
                    CallId = rCall,
                    SessionId = rSession,
                    Code = r.Code,
                    Message = ErrorLog.Truncate(r.Message, ErrorLog.MaxMessage),
                    SourceHash = sourceHash,
                    CreatedUtc = now,
                });
            }

            if (callId != null && version != null)
            {
                var row = await db.SourceStates.FindAsync(new object[] { tenantId, callId }, token);
                if (row == null)
                {
                    db.SourceStates.Add(new SourceStateRow
                    {
                        TenantId = tenantId,
                        CallId = callId,
                        State = SourceStates.Done,
                        AppliedVersion = null,
                        RejectedVersion = version,
                        Attempts = 0,
                        FirstSeenUtc = now,
                        UpdatedUtc = now,
                    });
                }
                else
                {
                    row.RejectedVersion = Math.Max(row.RejectedVersion ?? long.MinValue, version.Value);
                    if (row.State is SourceStates.Pending or SourceStates.Suspended)
                    {
                        row.State = SourceStates.Done;
                        row.Attempts = 0;
                        row.NextAttemptUtc = null;
                    }
                    row.UpdatedUtc = now;
                }
            }

            InboxTransitions.MarkDone(db, inboxId, now);
            await db.SaveChangesAsync(token);
        }, ct);

    public Task RecordDiscrepancyAsync(string tenantId, string callId, long version, string? appliedSetHash,
        string incomingSetHash, long inboxId, CancellationToken ct) =>
        _gate.RunAsync(async (db, token) =>
        {
            var now = UtcText.Now(_clock);
            using var stream = new MemoryStream();
            using (var w = new Utf8JsonWriter(stream))
            {
                w.WriteStartObject();
                w.WriteNumber("version", version);
                if (appliedSetHash == null) w.WriteNull("applied_set_hash"); else w.WriteString("applied_set_hash", appliedSetHash);
                w.WriteString("incoming_set_hash", incomingSetHash);
                w.WriteEndObject();
            }
            db.AuditLog.Add(new AuditLogRow
            {
                Ts = now,
                TenantId = tenantId,
                CallId = callId,
                Kind = AuditKinds.VersionDiscrepancy,
                Detail = Encoding.UTF8.GetString(stream.ToArray()),
            });
            InboxTransitions.MarkDone(db, inboxId, now);
            await db.SaveChangesAsync(token);
        }, ct);
}
