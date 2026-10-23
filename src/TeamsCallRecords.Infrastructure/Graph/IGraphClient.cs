namespace TeamsCallRecords.Infrastructure.Graph;

/// <summary>The only gateway to Microsoft Graph (TRP 3.14.3).</summary>
public interface IGraphClient
{
    Task<GraphFetchResult> GetCallRecordAsync(string callId, CancellationToken ct);
    Task<IReadOnlyList<GraphCallRecordRef>> ListCallRecordsAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct);
    Task<IReadOnlyList<GraphSubscriptionInfo>> ListSubscriptionsAsync(CancellationToken ct);
    Task<GraphSubscription> CreateSubscriptionAsync(GraphSubscriptionRequest request, CancellationToken ct);
    Task<GraphSubscription> RenewSubscriptionAsync(string subscriptionId, DateTimeOffset newExpiresUtc, CancellationToken ct);
    Task DeleteSubscriptionAsync(string subscriptionId, CancellationToken ct);
    Task SendActivityNotificationAsync(ActivityNotification notification, CancellationToken ct);
}
