using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TeamsCallRecords.Core;
using TeamsCallRecords.Infrastructure.Config;
using TeamsCallRecords.Infrastructure.Data;
using TeamsCallRecords.Infrastructure.Data.Entities;
using TeamsCallRecords.Infrastructure.Diagnostics;
using TeamsCallRecords.Infrastructure.Graph;
using TeamsCallRecords.Infrastructure.Queue;

namespace TeamsCallRecords.Infrastructure.Processing;

/// <summary>Processes one claimed inbox item (TRP 3.13.3, algorithm A2). Stateless and thread-safe.</summary>
public sealed class CallProcessor : ICallProcessor
{
    private const int MaxDecisionAttempts = 3;
    private static readonly TimeSpan MaxManualRange = TimeSpan.FromDays(7);

    private readonly IGraphClient _graph;
    private readonly IInboxStore _inbox;
    private readonly ISourceStateStore _states;
    private readonly ICdrApplier _applier;
    private readonly IReconciler _reconciler;
    private readonly IErrorLog _errors;
    private readonly IOptions<ProcessingOptions> _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<CallProcessor> _log;

    public CallProcessor(IGraphClient graph, IInboxStore inbox, ISourceStateStore states, ICdrApplier applier,
        IReconciler reconciler, IErrorLog errors, IOptions<ProcessingOptions> options, TimeProvider clock,
        ILogger<CallProcessor> log)
    {
        _graph = graph;
        _inbox = inbox;
        _states = states;
        _applier = applier;
        _reconciler = reconciler;
        _errors = errors;
        _options = options;
        _clock = clock;
        _log = log;
    }

    /// <summary>Hash stored in rejected.source_hash: canonical JSON hash, or SHA-256 of the raw text if it is not JSON.</summary>
    public static string RejectedSourceHash(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return Canonical.HashOf(doc.RootElement);
        }
        catch (JsonException)
        {
            return Canonical.Sha256Hex(json);
        }
    }

    public async Task<ProcessOutcome> ProcessAsync(InboxItem item, CancellationToken ct)
    {
        using var scope = _log.BeginScope(new Dictionary<string, object?>
        {
            ["component"] = Components.Processor,
            ["tenant_id"] = item.TenantId,
            ["call_id"] = item.CallId,
            ["inbox_id"] = item.Id,
        });
        try
        {
            return await ProcessCoreAsync(item, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (GraphException ex)
        {
            return await HandleAsync(item, ex, graphError: true, ct);
        }
        catch (Exception ex)
        {
            return await HandleAsync(item, ex, graphError: false, ct);
        }
    }

    private async Task<ProcessOutcome> HandleAsync(InboxItem item, Exception ex, bool graphError, CancellationToken ct)
    {
        try
        {
            var opt = _options.Value;
            var canRetry = item.Attempts < opt.MaxAttempts;
            var next = _clock.GetUtcNow() + opt.RetryDelayFor(Math.Max(1, item.Attempts));

            if (graphError)
            {
                var g = (GraphException)ex;
                if (g.IsTransient && canRetry)
                {
                    await _inbox.RescheduleAsync(item.Id, next, g.Message, false, ct);
                    return ProcessOutcome.Retry;
                }
                await _inbox.FailAsync(item.Id, g.Message, ct);
                await _errors.WriteAsync(Components.Graph, "graph request failed",
                    $"tenant_id={item.TenantId}; call_id={item.CallId}; status={(g.StatusCode?.ToString() ?? "none")}; attempts={item.Attempts}", ct);
                return ProcessOutcome.Failed;
            }

            _log.LogError("Unexpected processing error: {Type}: {Message}", ex.GetType().Name, ex.Message);
            try
            {
                await _errors.WriteAsync(Components.Processor, "unexpected processing error",
                    $"tenant_id={item.TenantId}; call_id={item.CallId}; exception={ex.GetType().Name}", ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception inner) { _log.LogError(inner, "error_log write failed"); }

            var message = ex.GetType().Name + ": " + ex.Message;
            if (canRetry)
            {
                await _inbox.RescheduleAsync(item.Id, next, message, false, ct);
                return ProcessOutcome.Retry;
            }
            await _inbox.FailAsync(item.Id, message, ct);
            return ProcessOutcome.Failed;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception handlerError)
        {
            // The item stays "processing" until its lease expires and is reclaimed.
            _log.LogError(handlerError, "Error handler failed for inbox item {InboxId}", item.Id);
            return ProcessOutcome.Retry;
        }
    }

    private async Task<ProcessOutcome> ProcessCoreAsync(InboxItem item, CancellationToken ct)
    {
        switch (item.Kind)
        {
            case InboxKinds.ChannelMessage:
                _log.LogInformation("Channel message received");
                await _inbox.CompleteAsync(item.Id, ct);
                return ProcessOutcome.Logged;

            case InboxKinds.Reconcile when item.CallId == null:
                return await ProcessReconcileAsync(item, ct);

            case InboxKinds.CallRecord or InboxKinds.Reconcile:
                break;

            default:
                await _inbox.FailAsync(item.Id, "INBOX_UNKNOWN_KIND", ct);
                return ProcessOutcome.Failed;
        }

        if (item.CallId == null)
        {
            await _inbox.FailAsync(item.Id, "INBOX_NO_CALL_ID", ct);
            await _errors.WriteAsync(Components.Processor, "inbox item without call_id", $"inbox_id={item.Id}", ct);
            return ProcessOutcome.Failed;
        }

        var callId = item.CallId;
        var fetched = await _graph.GetCallRecordAsync(callId, ct);

        for (var attempt = 1; ; attempt++)
        {
            var state = await _states.GetAsync(item.TenantId, callId, ct);

            if (fetched.Status is GraphFetchStatus.NotFound or GraphFetchStatus.PagesIncomplete)
            {
                if (state?.AppliedVersion != null)
                {
                    _log.LogInformation("Graph returned {Status} for an already applied call", fetched.Status);
                    await _inbox.CompleteAsync(item.Id, ct);
                    return ProcessOutcome.Duplicate;
                }
                return await RegisterIncompleteAsync(item, callId,
                    fetched.Status == GraphFetchStatus.NotFound ? "GRAPH_NOT_FOUND" : "GRAPH_PAGES_INCOMPLETE", ct);
            }

            var json = fetched.Json ?? "";
            var result = CallRecordConverter.Convert(json, item.TenantId);

            if (result.Rejections.Any(r => r.Code is RejectionCodes.BadJson or RejectionCodes.NoCallId or RejectionCodes.NoVersion))
            {
                await _states.RecordRejectionAsync(item.TenantId, result.CallId ?? callId, result.SourceVersion,
                    result.Rejections, RejectedSourceHash(json), item.Id, ct);
                return ProcessOutcome.Rejected;
            }

            var version = result.SourceVersion!.Value;
            var setHash = result.SessionSetHash ?? "";
            var action = VersionRules.Decide(state?.AppliedVersion, state?.SessionSetHash, version, setHash);
            if (state?.RejectedVersion > version) action = VersionAction.IgnoreLower;

            switch (action)
            {
                case VersionAction.IgnoreLower:
                    _log.LogInformation("Ignoring lower version {Version}", version);
                    await _inbox.CompleteAsync(item.Id, ct);
                    return ProcessOutcome.IgnoredLower;
                case VersionAction.DuplicateSame:
                    await _inbox.CompleteAsync(item.Id, ct);
                    return ProcessOutcome.Duplicate;
                case VersionAction.Discrepancy:
                    await _states.RecordDiscrepancyAsync(item.TenantId, callId, version, state?.SessionSetHash, setHash, item.Id, ct);
                    return ProcessOutcome.Discrepancy;
            }

            var transient = result.IncompleteReasons
                .Where(r => r is "NO_SESSIONS" or "SESSION_DATES_MISSING").ToList();
            if (transient.Count > 0)
                return await RegisterIncompleteAsync(item, callId, string.Join(",", transient), ct);

            if (!result.IsComplete)
            {
                if (state?.RejectedVersion >= version)
                {
                    await _inbox.CompleteAsync(item.Id, ct);
                    return ProcessOutcome.Duplicate;
                }
                await _states.RecordRejectionAsync(item.TenantId, result.CallId ?? callId, version,
                    result.Rejections, RejectedSourceHash(json), item.Id, ct);
                return ProcessOutcome.Rejected;
            }

            var applied = await _applier.ApplyAsync(new ApplyRequest(item.TenantId, result, item.Id), ct);
            if (applied.Status == ApplyStatus.Applied) return ProcessOutcome.Applied;
            if (attempt >= MaxDecisionAttempts) throw new InvalidOperationException("decision changed repeatedly");
        }
    }

    private async Task<ProcessOutcome> RegisterIncompleteAsync(InboxItem item, string callId, string reason, CancellationToken ct)
    {
        var p = await _states.RegisterIncompleteAsync(item.TenantId, callId, reason, item.Id, ct);
        return p.State switch
        {
            SourceStates.Suspended => ProcessOutcome.Suspended,
            SourceStates.Expired => ProcessOutcome.Expired,
            _ => ProcessOutcome.Pending,
        };
    }

    private async Task<ProcessOutcome> ProcessReconcileAsync(InboxItem item, CancellationToken ct)
    {
        if (!TryParseRange(item.Raw, out var range, out var requested))
        {
            await _inbox.FailAsync(item.Id, "RECONCILE_BAD_RANGE", ct);
            await _errors.WriteAsync(Components.Processor, "reconcile range rejected", $"inbox_id={item.Id}", ct);
            return ProcessOutcome.Failed;
        }

        if (requested) await _reconciler.ReconcileAsync(range.From, range.To, true, ct);
        else await _reconciler.ReconcileLookbackAsync(ct);

        await _inbox.CompleteAsync(item.Id, ct);
        return ProcessOutcome.ReconcileRun;
    }

    /// <summary>
    /// requested=false: all of from_utc/to_utc/force absent (normal lookback). requested=true with true result:
    /// a valid manual range. Returns false for any incomplete, damaged or invalid request.
    /// </summary>
    private static bool TryParseRange(string raw, out (DateTimeOffset From, DateTimeOffset To) range, out bool requested)
    {
        range = default;
        requested = false;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            var hasFrom = root.TryGetProperty("from_utc", out var fromEl);
            var hasTo = root.TryGetProperty("to_utc", out var toEl);
            var hasForce = root.TryGetProperty("force", out var forceEl);
            if (!hasFrom && !hasTo && !hasForce) return true;

            requested = true;
            if (!hasFrom || !hasTo || !hasForce) return false;
            if (fromEl.ValueKind != JsonValueKind.String || toEl.ValueKind != JsonValueKind.String) return false;
            if (forceEl.ValueKind != JsonValueKind.True) return false;
            if (!DateTimeOffset.TryParse(fromEl.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var from)) return false;
            if (!DateTimeOffset.TryParse(toEl.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var to)) return false;
            if (from >= to || to - from > MaxManualRange) return false;
            range = (from, to);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
