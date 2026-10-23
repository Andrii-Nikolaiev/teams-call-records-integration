namespace TeamsCallRecords.Infrastructure.Data.Entities;

/// <summary>Table cdr. Timestamps are UtcText strings.</summary>
public sealed class CdrRow
{
    public long CdrSeq { get; set; }
    public int SchemaVersion { get; set; }
    public string EventType { get; set; } = "";
    public string TenantId { get; set; } = "";
    public string CallId { get; set; } = "";
    public string SessionId { get; set; } = "";
    public long SourceVersion { get; set; }
    public string CallType { get; set; } = "";
    public string? CallerType { get; set; }
    public string? CallerId { get; set; }
    public string? CalleeType { get; set; }
    public string? CalleeId { get; set; }
    public string StartUtc { get; set; } = "";
    public string EndUtc { get; set; } = "";
    public long DurationS { get; set; }
    public string Modalities { get; set; } = "[]";
    public string Status { get; set; } = "";
    public string? TerminationReason { get; set; }
    public int SegmentCount { get; set; }
    public string AggregationStatus { get; set; } = "";
    public string SourceHash { get; set; } = "";
    public string? Extensions { get; set; }
    public string CreatedUtc { get; set; } = "";
    public string UpdatedUtc { get; set; } = "";
}
