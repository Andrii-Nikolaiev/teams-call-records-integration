using TeamsCallRecords.Core;

namespace TeamsCallRecords.Infrastructure.Processing;

public enum ProcessOutcome { Applied, IgnoredLower, Duplicate, Discrepancy, Rejected, Pending, Suspended, Expired, Retry, Failed, Logged, ReconcileRun }

public sealed record ApplyRequest(string TenantId, ConversionResult Result, long InboxId);
public enum ApplyStatus { Applied, DecisionChanged }
public sealed record ApplyResult(ApplyStatus Status, VersionAction? ChangedAction, int Inserted, int Updated, int Deleted, int OutboxCreated);
public sealed record PendingResult(string State, DateTimeOffset? NextAttemptUtc);
public sealed record SourceStateCounts(int Pending, int Suspended, int Expired);
