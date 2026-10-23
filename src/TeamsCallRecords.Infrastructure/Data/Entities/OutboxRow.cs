namespace TeamsCallRecords.Infrastructure.Data.Entities;

/// <summary>Table outbox.</summary>
public sealed class OutboxRow
{
    public long Id { get; set; }
    public string Kind { get; set; } = "";
    public string TenantId { get; set; } = "";
    public string CallId { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string Recipient { get; set; } = "";
    public string Payload { get; set; } = "";
    public string State { get; set; } = "";
    public int Attempts { get; set; }
    public string NextAttemptUtc { get; set; } = "";
    public string? LockedUntilUtc { get; set; }
    public string CreatedUtc { get; set; } = "";
    public string? SentUtc { get; set; }
    public string? LastError { get; set; }
}
