namespace TeamsCallRecords.Host.Web;

/// <summary>Operating mode of the Host (Graph:Mode), registered in Program.cs and reported by GET /api/status.</summary>
public sealed record HostMode(string Name)
{
    public const string Demo = "demo";
    public const string Live = "live";
}
