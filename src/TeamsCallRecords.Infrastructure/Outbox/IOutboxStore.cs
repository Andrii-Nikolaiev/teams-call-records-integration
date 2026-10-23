namespace TeamsCallRecords.Infrastructure.Outbox;

public interface IOutboxStore
{
    Task<OutboxItem?> ClaimNextAsync(CancellationToken ct);
    Task MarkSentAsync(long id, CancellationToken ct);
    /// <summary>Returns false when MaxAttempts is reached and the item became failed.</summary>
    Task<bool> RescheduleAsync(long id, string error, CancellationToken ct);
    Task FailAsync(long id, string error, CancellationToken ct);
    Task<OutboxStats> GetStatsAsync(CancellationToken ct);
}
