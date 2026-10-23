namespace TeamsCallRecords.Infrastructure.Queue;

public interface IInboxStore
{
    Task<InboxEnqueueResult> EnqueueAsync(IReadOnlyList<InboxNewItem> items, CancellationToken ct);
    Task<InboxItem?> ClaimNextAsync(CancellationToken ct);
    Task<DateTimeOffset?> RenewLeaseAsync(long id, DateTimeOffset expectedLockedUntilUtc, CancellationToken ct);
    Task CompleteAsync(long id, CancellationToken ct);
    Task RescheduleAsync(long id, DateTimeOffset nextAttemptUtc, string? error, bool resetAttempts, CancellationToken ct);
    Task FailAsync(long id, string error, CancellationToken ct);
    Task<InboxStats> GetStatsAsync(CancellationToken ct);
}
