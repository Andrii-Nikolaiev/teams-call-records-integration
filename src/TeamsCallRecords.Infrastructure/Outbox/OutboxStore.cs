using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TeamsCallRecords.Infrastructure.Config;
using TeamsCallRecords.Infrastructure.Data;
using TeamsCallRecords.Infrastructure.Data.Entities;

namespace TeamsCallRecords.Infrastructure.Outbox;

/// <summary>Outgoing notification queue on top of the outbox table (TRP 3.16.2).</summary>
public sealed class OutboxStore : IOutboxStore
{
    private readonly IWriteGate _gate;
    private readonly IDbContextFactory<TcriDbContext> _factory;
    private readonly IOptions<OutboxOptions> _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<OutboxStore> _log;

    public OutboxStore(IWriteGate gate, IDbContextFactory<TcriDbContext> factory, IOptions<OutboxOptions> options,
        TimeProvider clock, ILogger<OutboxStore> log)
    {
        _gate = gate;
        _factory = factory;
        _options = options;
        _clock = clock;
        _log = log;
    }

    public Task<OutboxItem?> ClaimNextAsync(CancellationToken ct) =>
        _gate.RunAsync<OutboxItem?>(async (db, token) =>
        {
            var nowTime = _clock.GetUtcNow();
            var now = UtcText.Format(nowTime);

            // An expired "sending" lease means the outcome is unknown: never resend automatically.
            var expired = await db.Database.ExecuteSqlInterpolatedAsync($@"
UPDATE outbox SET state = 'failed', last_error = 'OUTCOME_UNKNOWN: lease expired', locked_until_utc = NULL
WHERE state = 'sending' AND locked_until_utc < {now}", token);
            if (expired > 0) _log.LogWarning("{Count} outbox item(s) closed with unknown outcome (lease expired)", expired);

            var row = await db.OutboxItems.FromSqlInterpolated(
                    $"SELECT * FROM outbox WHERE state = 'new' AND next_attempt_utc <= {now}")
                .OrderBy(x => x.NextAttemptUtc).ThenBy(x => x.Id).Take(1).FirstOrDefaultAsync(token);
            if (row == null) return null;

            row.State = OutboxStates.Sending;
            row.LockedUntilUtc = UtcText.Format(nowTime.AddSeconds(_options.Value.LeaseSeconds));
            row.Attempts += 1;
            await db.SaveChangesAsync(token);
            return new OutboxItem(row.Id, row.TenantId, row.CallId, row.SessionId, row.Recipient, row.Payload, row.Attempts);
        }, ct);

    public Task MarkSentAsync(long id, CancellationToken ct) =>
        _gate.RunAsync(async (db, token) =>
        {
            var row = Find(db, id);
            row.State = OutboxStates.Sent;
            row.SentUtc = UtcText.Now(_clock);
            row.LockedUntilUtc = null;
            row.LastError = null;
            await db.SaveChangesAsync(token);
        }, ct);

    public Task<bool> RescheduleAsync(long id, string error, CancellationToken ct) =>
        _gate.RunAsync(async (db, token) =>
        {
            var row = Find(db, id);
            var opt = _options.Value;
            bool retry;
            if (row.Attempts >= opt.MaxAttempts)
            {
                row.State = OutboxStates.Failed;
                retry = false;
            }
            else
            {
                row.State = OutboxStates.New;
                row.NextAttemptUtc = UtcText.Format(_clock.GetUtcNow() + opt.BackoffFor(row.Attempts));
                retry = true;
            }
            row.LockedUntilUtc = null;
            row.LastError = error;
            await db.SaveChangesAsync(token);
            return retry;
        }, ct);

    public Task FailAsync(long id, string error, CancellationToken ct) =>
        _gate.RunAsync(async (db, token) =>
        {
            var row = Find(db, id);
            row.State = OutboxStates.Failed;
            row.LockedUntilUtc = null;
            row.LastError = error;
            await db.SaveChangesAsync(token);
        }, ct);

    public async Task<OutboxStats> GetStatsAsync(CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var pending = await db.OutboxItems.AsNoTracking()
            .CountAsync(x => x.State == OutboxStates.New || x.State == OutboxStates.Sending, ct);
        var failed = await db.OutboxItems.AsNoTracking().CountAsync(x => x.State == OutboxStates.Failed, ct);
        return new OutboxStats(pending, failed);
    }

    private static OutboxRow Find(TcriDbContext db, long id) =>
        db.OutboxItems.Find(id) ?? throw new InvalidOperationException($"outbox item {id} not found");
}
