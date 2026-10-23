using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TeamsCallRecords.Infrastructure.Config;
using TeamsCallRecords.Infrastructure.Data;
using TeamsCallRecords.Infrastructure.Data.Entities;
using TeamsCallRecords.Infrastructure.Graph;
using TeamsCallRecords.Infrastructure.Queue;

namespace TeamsCallRecords.Infrastructure.Processing;

/// <summary>Compares Graph with local state and enqueues calls that need a read (TRP 3.13.6).</summary>
public sealed class Reconciler : IReconciler
{
    private readonly IGraphClient _graph;
    private readonly ISourceStateStore _states;
    private readonly IInboxStore _inbox;
    private readonly IOptions<GraphOptions> _graphOptions;
    private readonly IOptions<ReconcileOptions> _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<Reconciler> _log;
    private long _lastRunTicks;   // 0 = never; kept in memory only

    public Reconciler(IGraphClient graph, ISourceStateStore states, IInboxStore inbox, IOptions<GraphOptions> graphOptions,
        IOptions<ReconcileOptions> options, TimeProvider clock, ILogger<Reconciler> log)
    {
        _graph = graph;
        _states = states;
        _inbox = inbox;
        _graphOptions = graphOptions;
        _options = options;
        _clock = clock;
        _log = log;
    }

    public DateTimeOffset? LastRunUtc
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastRunTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    /// <summary>First matching row of the NeedsFetch table decides.</summary>
    public static bool NeedsFetch(SourceStateRow? s, GraphCallRecordRef r, bool force)
    {
        if (s == null) return true;                                   // 1
        if (force) return true;                                       // 2
        if (s.State is SourceStates.Pending or SourceStates.Suspended) return false;   // 3
        if (s.State == SourceStates.Expired) return false;            // 4
        if (s.State == SourceStates.Done && r.Version == null) return true;            // 5
        if (s.State == SourceStates.Done && r.Version > Math.Max(s.AppliedVersion ?? -1, s.RejectedVersion ?? -1)) return true; // 6
        return false;                                                 // 7
    }

    public async Task<int> ReconcileAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, bool force, CancellationToken ct)
    {
        var tenant = _graphOptions.Value.TenantId;
        var refs = await _graph.ListCallRecordsAsync(fromUtc, toUtc, ct);
        var known = await _states.GetManyAsync(tenant, refs.Select(r => r.Id).ToList(), ct);

        var items = new List<InboxNewItem>();
        foreach (var r in refs)
        {
            known.TryGetValue(r.Id, out var state);
            if (NeedsFetch(state, r, force))
                items.Add(new InboxNewItem(InboxKinds.Reconcile, tenant, r.Id, RawFor(r)));
        }
        return await EnqueueAsync(items, ct);
    }

    public async Task<int> ReconcileLookbackAsync(CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var added = await ReconcileAsync(now.AddHours(-_options.Value.LookbackHours), now, force: false, ct);
        Interlocked.Exchange(ref _lastRunTicks, now.UtcTicks);
        return added;
    }

    public async Task<int> RecheckSuspendedAsync(CancellationToken ct)
    {
        var tenant = _graphOptions.Value.TenantId;
        var rows = await _states.ListSuspendedDueAsync(200, ct);
        var items = rows.Select(r => new InboxNewItem(InboxKinds.Reconcile, tenant, r.CallId, RecheckRaw(r))).ToList();
        return await EnqueueAsync(items, ct);
    }

    private async Task<int> EnqueueAsync(List<InboxNewItem> items, CancellationToken ct)
    {
        var result = await _inbox.EnqueueAsync(items, ct);
        if (result.Overloaded)
        {
            _log.LogWarning("Inbox is overloaded; reconciliation items were not enqueued");
            return 0;
        }
        return result.Added;
    }

    private static string RawFor(GraphCallRecordRef r)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("call_id", r.Id);
            if (r.Version == null) w.WriteNull("version"); else w.WriteNumber("version", r.Version.Value);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string RecheckRaw(SourceStateRow row)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("call_id", row.CallId);
            w.WriteString("recheck_at", row.NextAttemptUtc);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
