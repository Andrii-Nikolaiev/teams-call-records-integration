namespace TeamsCallRecords.Infrastructure.Processing;

public interface ICdrApplier
{
    Task<ApplyResult> ApplyAsync(ApplyRequest request, CancellationToken ct);
}
