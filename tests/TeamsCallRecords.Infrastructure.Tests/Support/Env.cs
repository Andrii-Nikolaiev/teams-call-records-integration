using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TeamsCallRecords.Infrastructure.Config;
using TeamsCallRecords.Infrastructure.Data;
using TeamsCallRecords.Infrastructure.Data.Entities;
using TeamsCallRecords.Infrastructure.Diagnostics;
using TeamsCallRecords.Infrastructure.Processing;
using TeamsCallRecords.Infrastructure.Queue;

namespace TeamsCallRecords.Infrastructure.Tests.Support;

/// <summary>A database plus real stores wired the way the host wires them, with fake Graph and reconciler.</summary>
public sealed class Env : IDisposable
{
    private int _counter;

    public TestDb Db { get; }
    public ProcessingOptions Processing { get; } = new();
    public GraphOptions Graph { get; } = new() { TenantId = TestDb.Tenant };
    public OutboxOptions Outbox { get; } = new();
    public InboxStore Inbox { get; }
    public SourceStateStore States { get; }
    public CdrApplier Applier { get; }
    public ErrorLog Errors { get; }
    public FakeGraphClient FakeGraph { get; } = new();
    public FakeReconciler FakeReconciler { get; } = new();

    public Env(DateTimeOffset? start = null)
    {
        Db = TestDb.Create(start);
        Inbox = new InboxStore(Db.Gate, Db.Factory, Options.Create(new InboxOptions()), Options.Create(Processing), Db.Clock);
        States = new SourceStateStore(Db.Gate, Db.Factory, Options.Create(Processing), Options.Create(Graph), Db.Clock);
        Applier = new CdrApplier(Db.Gate, Options.Create(Outbox), Db.Clock);
        Errors = new ErrorLog(Db.Gate, Db.Clock, NullLogger<ErrorLog>.Instance);
    }

    public CallProcessor Processor(ICdrApplier? applier = null) =>
        new(FakeGraph, Inbox, States, applier ?? Applier, FakeReconciler, Errors, Options.Create(Processing), Db.Clock,
            NullLogger<CallProcessor>.Instance);

    public DateTimeOffset Now => Db.Clock.GetUtcNow();
    public void Advance(TimeSpan by) => Db.Clock.Advance(by);

    /// <summary>Enqueues one item with unique content and claims it (the only due item).</summary>
    public async Task<InboxItem> EnqueueAndClaim(string? callId = "call-1", string kind = InboxKinds.CallRecord, string? raw = null)
    {
        raw ??= "{\"call_id\":\"" + callId + "\",\"n\":" + ++_counter + "}";
        await Inbox.EnqueueAsync(new[] { new InboxNewItem(kind, TestDb.Tenant, callId, raw) }, default);
        return (await Inbox.ClaimNextAsync(default))!;
    }

    public async Task SeedState(string callId, string state, long? applied = null, long? rejected = null, string? setHash = null,
        int attempts = 0, DateTimeOffset? firstSeen = null, DateTimeOffset? next = null)
    {
        var now = UtcText.Format(Now);
        await Db.Gate.RunAsync(async (ctx, ct) =>
        {
            ctx.SourceStates.Add(new SourceStateRow
            {
                TenantId = TestDb.Tenant,
                CallId = callId,
                State = state,
                AppliedVersion = applied,
                RejectedVersion = rejected,
                SessionSetHash = setHash,
                Attempts = attempts,
                FirstSeenUtc = UtcText.Format(firstSeen ?? Now),
                NextAttemptUtc = next == null ? null : UtcText.Format(next.Value),
                UpdatedUtc = now,
            });
            await ctx.SaveChangesAsync(ct);
        }, default);
    }

    public Task<List<SourceStateRow>> StateRows() => Db.ReadAsync(c => c.SourceStates.AsNoTracking().OrderBy(r => r.CallId).ToListAsync());
    public Task<List<CdrRow>> CdrRows() => Db.ReadAsync(c => c.Cdrs.AsNoTracking().OrderBy(r => r.CdrSeq).ToListAsync());
    public Task<List<OutboxRow>> OutboxRows() => Db.ReadAsync(c => c.OutboxItems.AsNoTracking().OrderBy(r => r.Id).ToListAsync());
    public Task<List<RejectedRow>> RejectedRows() => Db.ReadAsync(c => c.RejectedItems.AsNoTracking().OrderBy(r => r.Id).ToListAsync());
    public Task<List<AuditLogRow>> AuditRows() => Db.ReadAsync(c => c.AuditLog.AsNoTracking().OrderBy(r => r.Id).ToListAsync());
    public Task<List<ErrorLogRow>> ErrorRows() => Db.ReadAsync(c => c.ErrorLog.AsNoTracking().OrderBy(r => r.Id).ToListAsync());
    public Task<List<InboxRow>> InboxRows() => Db.ReadAsync(c => c.InboxItems.AsNoTracking().OrderBy(r => r.Id).ToListAsync());

    public void Dispose() => Db.Dispose();
}
