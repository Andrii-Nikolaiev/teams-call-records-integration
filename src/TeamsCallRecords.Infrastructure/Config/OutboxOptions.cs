namespace TeamsCallRecords.Infrastructure.Config;

/// <summary>Section "Outbox" (TRP 3.18).</summary>
public sealed class OutboxOptions
{
    public int MaxAttempts { get; set; } = 8;
    public int LeaseSeconds { get; set; } = 120;
    public string BackoffSeconds { get; set; } = "30,120,600,1800,3600";
    public int MaxAgeHours { get; set; } = 24;
}
