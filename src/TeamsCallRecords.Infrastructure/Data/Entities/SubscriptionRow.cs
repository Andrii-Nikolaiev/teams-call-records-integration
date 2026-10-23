namespace TeamsCallRecords.Infrastructure.Data.Entities;

/// <summary>Table subscription. Key: Id (Graph subscription id, not generated).</summary>
public sealed class SubscriptionRow
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Resource { get; set; } = "";
    public string NotificationUrl { get; set; } = "";
    public string ExpiresUtc { get; set; } = "";
    public string ClientStateHash { get; set; } = "";
    public string Status { get; set; } = "";
    public string CreatedUtc { get; set; } = "";
    public string? RenewedUtc { get; set; }
}
