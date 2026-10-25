using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Options;
using TeamsCallRecords.Infrastructure.Subscriptions;

namespace TeamsCallRecords.Host.Web;

/// <summary>An in-flight subscription: created in Graph, not yet saved in the database. Contains no secrets, only the clientState hash.</summary>
public sealed record PendingSubscription(string SubscriptionId, string TenantId, string Resource, string ClientStateHash, DateTimeOffset ExpiresUtc)
{
    /// <summary>True when tenant and resource are equal (case-insensitive, leading and trailing '/' ignored) and the clientState hash matches in constant time.</summary>
    public bool Matches(string tenantId, string resource, string clientState) =>
        string.Equals(TenantId, tenantId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Normalize(Resource), Normalize(resource), StringComparison.OrdinalIgnoreCase)
        && SubscriptionStore.Matches(ClientStateHash, clientState);

    private static string Normalize(string resource) => resource.Trim().Trim('/');
}

/// <summary>
/// In-memory markers of subscriptions that Graph has just created (TRP 3.23.3, ADR-0001). A marker lives
/// Webhook:SubscriptionCreationGraceSeconds (default 120); expired markers are removed when they are read or when a new marker is added.
/// A marker is the only reason to answer 503 (retry) instead of 403 for an unknown subscription id.
/// </summary>
public sealed class PendingSubscriptionRegistry : IPendingSubscriptionMarker
{
    private readonly ConcurrentDictionary<string, PendingSubscription> _items = new(StringComparer.Ordinal);
    private readonly IOptions<WebhookOptions> _options;
    private readonly TimeProvider _clock;

    public PendingSubscriptionRegistry(IOptions<WebhookOptions> options, TimeProvider clock)
    {
        _options = options;
        _clock = clock;
    }

    /// <summary>Number of stored markers, expired ones included until they are read or pruned.</summary>
    public int Count => _items.Count;

    public void Mark(string subscriptionId, string tenantId, string resource, string clientStateHash)
    {
        if (string.IsNullOrWhiteSpace(subscriptionId)) throw new ArgumentException("subscriptionId must not be empty", nameof(subscriptionId));
        var now = _clock.GetUtcNow();
        Prune(now);
        var expires = now.AddSeconds(_options.Value.SubscriptionCreationGraceSeconds);
        _items[subscriptionId] = new PendingSubscription(subscriptionId, tenantId, resource, clientStateHash, expires);
    }

    public void Clear(string subscriptionId) => _items.TryRemove(subscriptionId, out _);

    /// <summary>Returns a live marker; an expired marker is removed and not returned.</summary>
    public bool TryGet(string subscriptionId, [NotNullWhen(true)] out PendingSubscription? marker)
    {
        marker = null;
        if (string.IsNullOrEmpty(subscriptionId) || !_items.TryGetValue(subscriptionId, out var found)) return false;
        if (found.ExpiresUtc <= _clock.GetUtcNow())
        {
            _items.TryRemove(new KeyValuePair<string, PendingSubscription>(subscriptionId, found));
            return false;
        }
        marker = found;
        return true;
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var pair in _items)
            if (pair.Value.ExpiresUtc <= now)
                _items.TryRemove(pair);
    }
}
