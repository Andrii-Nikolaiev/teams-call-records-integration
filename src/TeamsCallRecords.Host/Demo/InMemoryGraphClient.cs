using System.Collections.Concurrent;
using System.Text.Json;
using TeamsCallRecords.Infrastructure.Graph;

namespace TeamsCallRecords.Host.Demo;

/// <summary>
/// Demo stand-in for Microsoft Graph: call records are put in by the demo scenario, notifications are collected
/// in memory. It makes the whole pipeline runnable without a Microsoft 365 tenant.
/// </summary>
public sealed class InMemoryGraphClient : IGraphClient
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _records = new();
    private readonly ConcurrentQueue<ActivityNotification> _sent = new();

    public IReadOnlyCollection<ActivityNotification> SentNotifications => _sent.ToArray();

    /// <summary>Adds or replaces a call record (its JSON must contain "id").</summary>
    public void Put(string callRecordJson)
    {
        using var doc = JsonDocument.Parse(callRecordJson);
        var id = doc.RootElement.GetProperty("id").GetString() ?? throw new ArgumentException("callRecord has no id");
        lock (_gate) _records[id] = callRecordJson;
    }

    public Task<GraphFetchResult> GetCallRecordAsync(string callId, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult(_records.TryGetValue(callId, out var json)
                ? new GraphFetchResult(GraphFetchStatus.Ok, json)
                : new GraphFetchResult(GraphFetchStatus.NotFound, null));
    }

    public Task<IReadOnlyList<GraphCallRecordRef>> ListCallRecordsAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        var result = new List<GraphCallRecordRef>();
        lock (_gate)
        {
            foreach (var (id, json) in _records)
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                long? version = root.TryGetProperty("version", out var v) && v.TryGetInt64(out var n) ? n : null;
                DateTimeOffset? start = root.TryGetProperty("startDateTime", out var s) && s.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(s.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var t)
                    ? t : null;
                if (start == null || (start >= fromUtc && start < toUtc))
                    result.Add(new GraphCallRecordRef(id, version, start));
            }
        }
        return Task.FromResult<IReadOnlyList<GraphCallRecordRef>>(result);
    }

    public Task SendActivityNotificationAsync(ActivityNotification notification, CancellationToken ct)
    {
        _sent.Enqueue(notification);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<GraphSubscriptionInfo>> ListSubscriptionsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<GraphSubscriptionInfo>>(Array.Empty<GraphSubscriptionInfo>());

    public Task<GraphSubscription> CreateSubscriptionAsync(GraphSubscriptionRequest request, CancellationToken ct) =>
        throw new NotSupportedException("Subscriptions are not available in demo mode");

    public Task<GraphSubscription> RenewSubscriptionAsync(string subscriptionId, DateTimeOffset newExpiresUtc, CancellationToken ct) =>
        throw new NotSupportedException("Subscriptions are not available in demo mode");

    public Task DeleteSubscriptionAsync(string subscriptionId, CancellationToken ct) =>
        throw new NotSupportedException("Subscriptions are not available in demo mode");
}
