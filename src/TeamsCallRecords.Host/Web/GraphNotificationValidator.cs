using Microsoft.Extensions.Options;
using TeamsCallRecords.Infrastructure.Config;
using TeamsCallRecords.Infrastructure.Data.Entities;
using TeamsCallRecords.Infrastructure.Subscriptions;

namespace TeamsCallRecords.Host.Web;

public enum NotificationVerdict
{
    /// <summary>The element comes from a subscription of this instance.</summary>
    Valid,

    /// <summary>The subscription was created a moment ago and is not saved yet: answer 503 so that Graph retries.</summary>
    TemporarilyUnknownSubscription,

    /// <summary>Unknown subscription, other tenant, other resource or wrong clientState.</summary>
    PermanentlyInvalid,
}

public interface IGraphNotificationValidator
{
    /// <summary>True when subscriptions are managed (Graph:NotificationUrl is set), false in demo/polling mode with one fixed clientState.</summary>
    bool IsManaged { get; }

    /// <summary>
    /// Classifies one element of a notification (TRP 3.23.3, ADR-0001 D2). Lifecycle notifications carry no resource, so for them
    /// <paramref name="resourceRequired"/> is false; a resource that is present is still compared. Throws when the database cannot be read.
    /// </summary>
    Task<NotificationVerdict> ClassifyAsync(string? subscriptionId, string? tenantId, string? resource, string? clientState,
        bool resourceRequired, CancellationToken ct);
}

/// <summary>
/// Managed mode: the subscription must be in the database (active or renewing), with the same tenant and resource, and the supplied
/// clientState must match the stored hash. A subscription that is not in the database yet is trusted only while a marker from
/// <see cref="PendingSubscriptionRegistry"/> exists. Demo mode: the old check against Webhook:ClientState.
/// </summary>
public sealed class GraphNotificationValidator : IGraphNotificationValidator
{
    private readonly ISubscriptionStore _store;
    private readonly PendingSubscriptionRegistry _pending;
    private readonly IOptions<GraphOptions> _graph;
    private readonly IOptions<WebhookOptions> _web;

    public GraphNotificationValidator(ISubscriptionStore store, PendingSubscriptionRegistry pending,
        IOptions<GraphOptions> graph, IOptions<WebhookOptions> web)
    {
        _store = store;
        _pending = pending;
        _graph = graph;
        _web = web;
    }

    public bool IsManaged => !string.IsNullOrWhiteSpace(_graph.Value.NotificationUrl);

    public async Task<NotificationVerdict> ClassifyAsync(string? subscriptionId, string? tenantId, string? resource, string? clientState,
        bool resourceRequired, CancellationToken ct)
    {
        if (!IsManaged)
        {
            var legacyOk = SecretCompare.Equal(clientState, _web.Value.ClientState)
                           && (tenantId == null || string.Equals(tenantId, _graph.Value.TenantId, StringComparison.OrdinalIgnoreCase));
            return legacyOk ? NotificationVerdict.Valid : NotificationVerdict.PermanentlyInvalid;
        }

        if (string.IsNullOrEmpty(subscriptionId) || string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(clientState)
            || (resourceRequired && string.IsNullOrEmpty(resource)))
            return NotificationVerdict.PermanentlyInvalid;
        if (!string.Equals(tenantId, _graph.Value.TenantId, StringComparison.OrdinalIgnoreCase))
            return NotificationVerdict.PermanentlyInvalid;

        var row = await _store.GetAsync(subscriptionId, ct);
        if (row != null)
        {
            var statusOk = row.Status == SubscriptionStatuses.Active || row.Status == SubscriptionStatuses.Renewing;
            var resourceOk = string.IsNullOrEmpty(resource) || ResourceMatch.Covers(row.Resource, resource);
            return statusOk && resourceOk && SubscriptionStore.Matches(row.ClientStateHash, clientState)
                ? NotificationVerdict.Valid
                : NotificationVerdict.PermanentlyInvalid;
        }

        // Not in the database: only a fresh marker made by this process may make the answer temporary.
        if (_pending.TryGet(subscriptionId, out var marker))
        {
            var pendingResource = string.IsNullOrEmpty(resource) ? marker.Resource : resource;
            return marker.Matches(tenantId, pendingResource, clientState)
                ? NotificationVerdict.TemporarilyUnknownSubscription
                : NotificationVerdict.PermanentlyInvalid;
        }
        return NotificationVerdict.PermanentlyInvalid;
    }
}
