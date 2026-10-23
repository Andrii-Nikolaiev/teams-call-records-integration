namespace TeamsCallRecords.Infrastructure.Config;

/// <summary>Section "Graph" (TRP 3.18). Only the settings used so far.</summary>
public sealed class GraphOptions
{
    public string TenantId { get; set; } = "";
    public int RetentionDays { get; set; } = 30;

    public IEnumerable<string> Validate()
    {
        if (string.IsNullOrWhiteSpace(TenantId)) yield return "Graph:TenantId must not be empty";
        if (RetentionDays < 1 || RetentionDays > 365) yield return "Graph:RetentionDays must be 1..365";
    }
}
