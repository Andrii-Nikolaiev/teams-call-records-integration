namespace TeamsCallRecords.Infrastructure.Subscriptions;

/// <summary>
/// Lets SubscriptionService tell the webhook that a subscription was just created in Graph and is not saved yet (TRP 3.23.3, ADR-0001 DV-2).
/// The implementation lives in the Host, so the dependency points from Host to Infrastructure only. Secrets are never passed: only the hash.
/// </summary>
public interface IPendingSubscriptionMarker
{
    /// <summary>Registers a trusted marker for a subscription created through a successful Graph response.</summary>
    void Mark(string subscriptionId, string tenantId, string resource, string clientStateHash);

    /// <summary>Removes the marker (after the row was saved).</summary>
    void Clear(string subscriptionId);
}
