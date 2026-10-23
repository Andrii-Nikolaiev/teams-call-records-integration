namespace TeamsCallRecords.Infrastructure.Data.Entities;

/// <summary>Table audit_log.</summary>
public sealed class AuditLogRow
{
    public long Id { get; set; }
    public string Ts { get; set; } = "";
    public string TenantId { get; set; } = "";
    public string CallId { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Detail { get; set; } = "";
}
