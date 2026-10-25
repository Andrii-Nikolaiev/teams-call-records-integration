namespace TeamsCallRecords.Infrastructure.Config;

/// <summary>Section "Graph" (TRP 3.14.2).</summary>
public sealed class GraphOptions
{
    public string TenantId { get; set; } = "";
    public string ClientId { get; set; } = "";
    /// <summary>Taken from the environment (Graph__ClientSecret); never stored in files.</summary>
    public string ClientSecret { get; set; } = "";
    public string NotificationUrl { get; set; } = "";
    /// <summary>Taken from the environment (Graph__ClientState); never stored in files.</summary>
    public string ClientState { get; set; } = "";
    public int RetentionDays { get; set; } = 30;
    public string BaseUrl { get; set; } = "https://graph.microsoft.com/v1.0";
    public string Scope { get; set; } = "https://graph.microsoft.com/.default";
    public int RequestTimeoutSeconds { get; set; } = 30;
    public int TotalTimeoutSeconds { get; set; } = 120;
    public int RetryMaxAttempts { get; set; } = 3;
    public int RetryBaseDelayMilliseconds { get; set; } = 500;
    public int MaxPages { get; set; } = 100;
    public string ActivityTopicWebUrl { get; set; } = "https://teams.microsoft.com/";

    /// <summary>Rules that hold in every mode (demo and live).</summary>
    public IEnumerable<string> Validate()
    {
        if (string.IsNullOrWhiteSpace(TenantId)) yield return "Graph:TenantId must not be empty";
        if (RetentionDays < 1 || RetentionDays > 365) yield return "Graph:RetentionDays must be 1..365";
        if (!IsHttpsUrl(BaseUrl)) yield return "Graph:BaseUrl must be an absolute HTTPS address";
        if (string.IsNullOrWhiteSpace(Scope)) yield return "Graph:Scope must not be empty";
        if (RequestTimeoutSeconds < 1 || RequestTimeoutSeconds > 300) yield return "Graph:RequestTimeoutSeconds must be 1..300";
        if (TotalTimeoutSeconds < RequestTimeoutSeconds) yield return "Graph:TotalTimeoutSeconds must be >= Graph:RequestTimeoutSeconds";
        if (RetryMaxAttempts < 0 || RetryMaxAttempts > 10) yield return "Graph:RetryMaxAttempts must be 0..10";
        if (RetryBaseDelayMilliseconds < 0) yield return "Graph:RetryBaseDelayMilliseconds must not be negative";
        if (MaxPages < 1 || MaxPages > 1000) yield return "Graph:MaxPages must be 1..1000";
    }

    /// <summary>
    /// Extra rules for the real Graph client (Graph:Mode=Live). NotificationUrl and ClientState are not
    /// checked here: they are used by subscription management, which is not implemented yet.
    /// </summary>
    public IEnumerable<string> ValidateLive()
    {
        if (string.IsNullOrWhiteSpace(ClientId)) yield return "Graph:ClientId must not be empty";
        if (string.IsNullOrWhiteSpace(ClientSecret)) yield return "Graph:ClientSecret must not be empty (set the environment variable Graph__ClientSecret)";
        if (!Guid.TryParse(TenantId, out _)) yield return "Graph:TenantId must be a GUID";
        if (!Guid.TryParse(ClientId, out _)) yield return "Graph:ClientId must be a GUID";
    }

    private static bool IsHttpsUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
}
