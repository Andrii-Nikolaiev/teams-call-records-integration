namespace TeamsCallRecords.Infrastructure.Config;

/// <summary>Section "Graph" (TRP 3.18). Only the settings used so far.</summary>
public sealed class GraphOptions
{
    public string TenantId { get; set; } = "";
    public int RetentionDays { get; set; } = 30;
}
