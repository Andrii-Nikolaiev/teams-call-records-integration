using Microsoft.Extensions.Logging;
using TeamsCallRecords.Infrastructure.Data.Entities;
using TeamsCallRecords.Infrastructure.Diagnostics;
using TeamsCallRecords.Infrastructure.Graph;

namespace TeamsCallRecords.Infrastructure.Outbox;

public interface IOutboxSender
{
    /// <summary>Returns true if an item was claimed (the caller calls again without delay).</summary>
    Task<bool> RunOnceAsync(CancellationToken ct);
}

/// <summary>Sends one activity notification per call (TRP 3.16.3). An uncertain outcome is never retried.</summary>
public sealed class OutboxSender : IOutboxSender
{
    private readonly IOutboxStore _store;
    private readonly IGraphClient _graph;
    private readonly IErrorLog _errors;
    private readonly TimeProvider _clock;
    private readonly ILogger<OutboxSender> _log;

    public OutboxSender(IOutboxStore store, IGraphClient graph, IErrorLog errors, TimeProvider clock, ILogger<OutboxSender> log)
    {
        _store = store;
        _graph = graph;
        _errors = errors;
        _clock = clock;
        _log = log;
    }

    public async Task<bool> RunOnceAsync(CancellationToken ct)
    {
        var item = await _store.ClaimNextAsync(ct);
        if (item == null) return false;

        ActivityPayload payload;
        try
        {
            payload = ActivityPayload.Parse(item.Payload);
        }
        catch (FormatException)
        {
            await _store.FailAsync(item.Id, "bad payload", ct);
            await WriteErrorAsync(item, null, ct);
            return true;
        }

        var notification = new ActivityNotification(
            item.Recipient,
            payload.ActivityType,
            "Teams call",
            $"Call {payload.CallType}, {payload.DurationSeconds} s, {payload.Status}",
            new Dictionary<string, string>
            {
                ["callType"] = payload.CallType,
                ["status"] = payload.Status,
                ["duration"] = payload.DurationSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["startUtc"] = payload.StartUtc,
            });

        try
        {
            await _graph.SendActivityNotificationAsync(notification, ct);
            await _store.MarkSentAsync(item.Id, ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (GraphException ex) when (!ex.OutcomeUnknown)
        {
            if (ex.IsTransient)
            {
                var retry = await _store.RescheduleAsync(item.Id, ex.Message, ct);
                if (!retry) await WriteErrorAsync(item, null, ct);
            }
            else
            {
                await _store.FailAsync(item.Id, ex.Message, ct);
                await WriteErrorAsync(item, null, ct);
            }
            return true;
        }
        catch (Exception ex)
        {
            // unknown outcome: the server may have delivered the notification, so no automatic retry
            var reason = ex is GraphException g ? g.Message : ex.GetType().Name;
            _log.LogWarning("Outbox item {Id}: outcome unknown ({Reason})", item.Id, ex.GetType().Name);
            await _store.FailAsync(item.Id, "OUTCOME_UNKNOWN: " + reason, ct);
            await WriteErrorAsync(item, "unknown", ct);
            return true;
        }
    }

    private Task WriteErrorAsync(OutboxItem item, string? outcome, CancellationToken ct) =>
        _errors.WriteAsync(Components.Outbox, "outbox item failed",
            $"id={item.Id}; call_id={item.CallId}; session_id={item.SessionId}; attempts={item.Attempts}"
            + (outcome == null ? "" : $"; outcome={outcome}"), ct);
}
