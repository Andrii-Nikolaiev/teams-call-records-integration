namespace TeamsCallRecords.Infrastructure.Outbox;

public sealed record OutboxItem(long Id, string TenantId, string CallId, string SessionId, string Recipient, string Payload, int Attempts);
public sealed record OutboxStats(int Pending, int Failed);
