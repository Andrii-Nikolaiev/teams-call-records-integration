namespace TeamsCallRecords.Infrastructure.Data.Entities;

/// <summary>Table inbox.</summary>
public sealed class InboxRow
{
    public long Id { get; set; }
    public string ElementHash { get; set; } = "";
    public string Kind { get; set; } = "";
    public string TenantId { get; set; } = "";
    public string? CallId { get; set; }
    public string Raw { get; set; } = "";
    public string State { get; set; } = "";
    public int Attempts { get; set; }
    public string NextAttemptUtc { get; set; } = "";
    public string? LockedUntilUtc { get; set; }
    public string ReceivedUtc { get; set; } = "";
    public string? DoneUtc { get; set; }
    public string? LastError { get; set; }
}
