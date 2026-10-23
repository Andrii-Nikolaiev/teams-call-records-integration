namespace TeamsCallRecords.Infrastructure.Data.Entities;

/// <summary>Table rejected.</summary>
public sealed class RejectedRow
{
    public long Id { get; set; }
    public string TenantId { get; set; } = "";
    public string? CallId { get; set; }
    public string? SessionId { get; set; }
    public string Code { get; set; } = "";
    public string Message { get; set; } = "";
    public string? SourceHash { get; set; }
    public string CreatedUtc { get; set; } = "";
}
