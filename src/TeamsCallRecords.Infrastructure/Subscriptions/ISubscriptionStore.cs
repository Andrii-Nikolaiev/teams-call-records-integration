using TeamsCallRecords.Infrastructure.Data.Entities;

namespace TeamsCallRecords.Infrastructure.Subscriptions;

/// <summary>Storage of Graph subscriptions known to this instance (TRP 3.15.2). The clientState itself is never stored, only its hash.</summary>
public interface ISubscriptionStore
{
    /// <summary>All rows, sorted by created_utc.</summary>
    Task<IReadOnlyList<SubscriptionRow>> ListAsync(CancellationToken ct);

    /// <summary>The row or null.</summary>
    Task<SubscriptionRow?> GetAsync(string id, CancellationToken ct);

    /// <summary>Inserts the row, or replaces all its columns when a row with the same id exists.</summary>
    Task UpsertAsync(SubscriptionRow row, CancellationToken ct);

    /// <summary>Changes the status; does nothing when the row is absent.</summary>
    Task SetStatusAsync(string id, string status, CancellationToken ct);

    /// <summary>Deletes the row; does nothing when it is absent.</summary>
    Task DeleteAsync(string id, CancellationToken ct);
}
