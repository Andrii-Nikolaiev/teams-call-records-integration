namespace TeamsCallRecords.Infrastructure.Data.Entities;

/// <summary>Table source_state. Key: (TenantId, CallId).</summary>
public sealed class SourceStateRow
{
    public string TenantId { get; set; } = "";
    public string CallId { get; set; } = "";
    public long? AppliedVersion { get; set; }
    public long? RejectedVersion { get; set; }
    public string? SessionSetHash { get; set; }
    public string State { get; set; } = "";
    public int Attempts { get; set; }
    public string? NextAttemptUtc { get; set; }
    public string FirstSeenUtc { get; set; } = "";
    public string? LastError { get; set; }
    public string UpdatedUtc { get; set; } = "";
}
