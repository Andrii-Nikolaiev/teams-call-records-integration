namespace TeamsCallRecords.Infrastructure.Subscriptions;

/// <summary>Creates and keeps alive the Graph subscriptions (TRP 3.15.3, ADR-0001).</summary>
public interface ISubscriptionService
{
    /// <summary>
    /// Makes sure every desired subscription exists and is not about to expire. Returns true when an active row exists for each of them.
    /// Graph errors are written to error_log and not thrown. Requires Graph:NotificationUrl and Graph:ClientState to be set.
    /// </summary>
    Task<bool> EnsureAsync(CancellationToken ct);

    /// <summary>Reacts to a lifecycle notification (reauthorizationRequired, subscriptionRemoved, missed). Graph errors are written to error_log and not thrown.</summary>
    Task HandleLifecycleAsync(string subscriptionId, string lifecycleEvent, CancellationToken ct);
}
