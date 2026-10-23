namespace TeamsCallRecords.Infrastructure.Processing;

public interface IReconciler
{
    DateTimeOffset? LastRunUtc { get; }
    Task<int> ReconcileAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, bool force, CancellationToken ct);
    Task<int> ReconcileLookbackAsync(CancellationToken ct);
    Task<int> RecheckSuspendedAsync(CancellationToken ct);
}
