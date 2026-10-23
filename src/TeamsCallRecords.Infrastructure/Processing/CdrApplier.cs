using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TeamsCallRecords.Core;
using TeamsCallRecords.Infrastructure.Config;
using TeamsCallRecords.Infrastructure.Data;
using TeamsCallRecords.Infrastructure.Data.Entities;
using TeamsCallRecords.Infrastructure.Outbox;
using TeamsCallRecords.Infrastructure.Queue;

namespace TeamsCallRecords.Infrastructure.Processing;

/// <summary>Atomic replacement of a call's CDR set together with state, outbox and inbox closing (TRP 3.13.5).</summary>
public sealed class CdrApplier : ICdrApplier
{
    private readonly IWriteGate _gate;
    private readonly IOptions<OutboxOptions> _outbox;
    private readonly TimeProvider _clock;

    public CdrApplier(IWriteGate gate, IOptions<OutboxOptions> outbox, TimeProvider clock)
    {
        _gate = gate;
        _outbox = outbox;
        _clock = clock;
    }

    public Task<ApplyResult> ApplyAsync(ApplyRequest request, CancellationToken ct) =>
        _gate.RunAsync(async (db, token) =>
        {
            var result = request.Result;
            var tenant = request.TenantId;
            var callId = result.CallId ?? result.Cdrs[0].CallId;
            var version = result.SourceVersion ?? result.Cdrs[0].SourceVersion;
            var setHash = result.SessionSetHash ?? "";
            var nowTime = _clock.GetUtcNow();
            var now = UtcText.Format(nowTime);

            var state = await db.SourceStates.FindAsync(new object[] { tenant, callId }, token);

            var action = VersionRules.Decide(state?.AppliedVersion, state?.SessionSetHash, version, setHash);
            if (state?.RejectedVersion > version) action = VersionAction.IgnoreLower;
            if (action != VersionAction.Apply)
                return new ApplyResult(ApplyStatus.DecisionChanged, action, 0, 0, 0, 0);

            var existing = await db.Cdrs.Where(r => r.TenantId == tenant && r.CallId == callId).ToListAsync(token);
            var oldSessions = existing.Select(r => r.SessionId).ToHashSet();
            var newSessions = result.Cdrs.Select(c => c.SessionId).ToHashSet();

            int inserted = 0, updated = 0, deleted = 0, outboxCreated = 0;
            foreach (var cdr in result.Cdrs)
            {
                var row = CdrMapping.ToRow(cdr, now);
                var old = existing.FirstOrDefault(e => e.SessionId == cdr.SessionId);
                if (old != null) { CdrMapping.CopyOnUpdate(old, row, now); updated++; }
                else { db.Cdrs.Add(row); inserted++; }
            }
            foreach (var old in existing.Where(e => !newSessions.Contains(e.SessionId)))
            {
                db.Cdrs.Remove(old);
                deleted++;
            }

            var oldestEnd = nowTime.AddHours(-_outbox.Value.MaxAgeHours);
            foreach (var cdr in result.Cdrs)
            {
                if (oldSessions.Contains(cdr.SessionId) || cdr.EndUtc < oldestEnd) continue;
                var recipients = new[] { cdr.Caller, cdr.Callee }
                    .Where(p => p != null && p.Type == "user" && !string.IsNullOrEmpty(p.Id))
                    .Select(p => p!.Id).Distinct().ToList();
                foreach (var recipient in recipients)
                {
                    var sessionId = cdr.SessionId;
                    var exists = await db.OutboxItems.AnyAsync(o => o.Kind == OutboxKinds.Activity && o.TenantId == tenant
                        && o.CallId == callId && o.SessionId == sessionId && o.Recipient == recipient, token);
                    if (exists) continue;
                    db.OutboxItems.Add(new OutboxRow
                    {
                        Kind = OutboxKinds.Activity,
                        TenantId = tenant,
                        CallId = callId,
                        SessionId = sessionId,
                        Recipient = recipient,
                        Payload = ActivityPayload.Create(cdr).ToJson(),
                        State = OutboxStates.New,
                        Attempts = 0,
                        NextAttemptUtc = now,
                        CreatedUtc = now,
                    });
                    outboxCreated++;
                }
            }

            if (state == null)
            {
                state = new SourceStateRow { TenantId = tenant, CallId = callId, FirstSeenUtc = now };
                db.SourceStates.Add(state);
            }
            state.AppliedVersion = version;
            state.SessionSetHash = setHash;
            state.State = SourceStates.Done;
            state.Attempts = 0;
            state.NextAttemptUtc = null;
            state.LastError = null;
            state.UpdatedUtc = now;

            InboxTransitions.MarkDone(db, request.InboxId, now);
            await db.SaveChangesAsync(token);
            return new ApplyResult(ApplyStatus.Applied, null, inserted, updated, deleted, outboxCreated);
        }, ct);
}
