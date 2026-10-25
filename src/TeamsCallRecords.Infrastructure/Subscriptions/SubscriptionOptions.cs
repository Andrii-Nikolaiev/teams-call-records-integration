namespace TeamsCallRecords.Infrastructure.Subscriptions;

/// <summary>Section "Subscriptions" (TRP 3.15.1, 3.18).</summary>
public sealed class SubscriptionOptions
{
    /// <summary>Call record subscription lifetime in minutes. The Graph limit for callRecords is 4230 minutes (under three days).</summary>
    public int CallRecordsLifetimeMinutes { get; set; } = 4230;

    /// <summary>Channel message subscription lifetime in minutes.</summary>
    public int ChannelMessagesLifetimeMinutes { get; set; } = 60;

    /// <summary>Team for the channel message subscription (F3). Empty together with <see cref="ChannelId"/> disables it.</summary>
    public string TeamId { get; set; } = "";

    /// <summary>Channel for the channel message subscription (F3).</summary>
    public string ChannelId { get; set; } = "";

    /// <summary>Interval between renewal checks (SubscriptionWorker).</summary>
    public int RenewCheckMinutes { get; set; } = 5;

    public IEnumerable<string> Validate()
    {
        if (CallRecordsLifetimeMinutes < 10 || CallRecordsLifetimeMinutes > 43_200)
            yield return "Subscriptions:CallRecordsLifetimeMinutes must be 10..43200";
        if (ChannelMessagesLifetimeMinutes < 10 || ChannelMessagesLifetimeMinutes > 43_200)
            yield return "Subscriptions:ChannelMessagesLifetimeMinutes must be 10..43200";
        if (RenewCheckMinutes < 1 || RenewCheckMinutes > 60)
            yield return "Subscriptions:RenewCheckMinutes must be 1..60";
        if (string.IsNullOrWhiteSpace(TeamId) != string.IsNullOrWhiteSpace(ChannelId))
            yield return "Subscriptions:TeamId and Subscriptions:ChannelId must both be specified or both empty";
    }
}
