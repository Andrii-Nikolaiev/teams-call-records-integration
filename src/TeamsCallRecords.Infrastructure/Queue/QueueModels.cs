namespace TeamsCallRecords.Infrastructure.Queue;

public sealed record InboxNewItem(string Kind, string TenantId, string? CallId, string Raw);
public sealed record InboxEnqueueResult(int Added, int Duplicates, bool Overloaded);
public sealed record InboxItem(long Id, string Kind, string TenantId, string? CallId, string Raw, int Attempts, DateTimeOffset ReceivedUtc,
    DateTimeOffset? LockedUntilUtc = null);
public sealed record InboxStats(int Pending, long OldestPendingAgeSeconds, int Failed);
