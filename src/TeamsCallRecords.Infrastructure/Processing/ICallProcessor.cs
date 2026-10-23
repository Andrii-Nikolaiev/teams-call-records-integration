using TeamsCallRecords.Infrastructure.Queue;

namespace TeamsCallRecords.Infrastructure.Processing;

public interface ICallProcessor
{
    Task<ProcessOutcome> ProcessAsync(InboxItem item, CancellationToken ct);
}
