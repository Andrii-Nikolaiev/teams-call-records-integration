using TeamsCallRecords.Core;
using TeamsCallRecords.Infrastructure.Data.Entities;

namespace TeamsCallRecords.Infrastructure.Processing;

public interface ISourceStateStore
{
    Task<SourceStateRow?> GetAsync(string tenantId, string callId, CancellationToken ct);
    Task<IReadOnlyDictionary<string, SourceStateRow>> GetManyAsync(string tenantId, IReadOnlyCollection<string> callIds, CancellationToken ct);
    Task<SourceStateCounts> GetCountsAsync(CancellationToken ct);
    Task<IReadOnlyList<SourceStateRow>> ListSuspendedDueAsync(int limit, CancellationToken ct);
    Task<PendingResult> RegisterIncompleteAsync(string tenantId, string callId, string reason, long inboxId, CancellationToken ct);
    Task RecordRejectionAsync(string tenantId, string? callId, long? version, IReadOnlyList<Rejection> rejections, string sourceHash, long inboxId, CancellationToken ct);
    Task RecordDiscrepancyAsync(string tenantId, string callId, long version, string? appliedSetHash, string incomingSetHash, long inboxId, CancellationToken ct);
}
