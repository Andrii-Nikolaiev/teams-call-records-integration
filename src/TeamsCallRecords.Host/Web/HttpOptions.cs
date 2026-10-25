namespace TeamsCallRecords.Host.Web;

/// <summary>Section "Api" (TRP 3.18): access to the REST interface.</summary>
public sealed class ApiOptions
{
    /// <summary>Shared key expected in the X-TCRI-Key header. Demo value in appsettings.json; never use it in production.</summary>
    public string Key { get; set; } = "";
    public int MaxPageSize { get; set; } = 100;

    public IEnumerable<string> Validate()
    {
        if (string.IsNullOrWhiteSpace(Key) || Key.Length < 16) yield return "Api:Key must be at least 16 characters";
        if (MaxPageSize < 1 || MaxPageSize > 1000) yield return "Api:MaxPageSize must be 1..1000";
    }
}

/// <summary>Section "Webhook" (TRP 3.18): Graph change notifications.</summary>
public sealed class WebhookOptions
{
    public int MaxBodyBytes { get; set; } = 1_048_576;
    public int MaxBatchItems { get; set; } = 100;
    /// <summary>Showcase simplification: one fixed clientState instead of per-subscription values (there is no subscription store yet).</summary>
    public string ClientState { get; set; } = "";
    /// <summary>How long a just-created subscription may be unknown to the database before notifications for it are rejected (TRP 3.23.3).</summary>
    public int SubscriptionCreationGraceSeconds { get; set; } = 120;

    public IEnumerable<string> Validate()
    {
        if (MaxBodyBytes < 1024 || MaxBodyBytes > 10_485_760) yield return "Webhook:MaxBodyBytes must be 1024..10485760";
        if (MaxBatchItems < 1 || MaxBatchItems > 1000) yield return "Webhook:MaxBatchItems must be 1..1000";
        if (SubscriptionCreationGraceSeconds < 1 || SubscriptionCreationGraceSeconds > 300)
            yield return "Webhook:SubscriptionCreationGraceSeconds must be 1..300";
        if (string.IsNullOrWhiteSpace(ClientState) || ClientState.Length < 8 || ClientState.Length > 128)
            yield return "Webhook:ClientState must be 8..128 characters";
    }
}

/// <summary>Section "Demo": demo-only endpoints (feed the in-memory Graph stand-in, show sent notifications).</summary>
public sealed class DemoOptions
{
    public bool Enabled { get; set; }
}
