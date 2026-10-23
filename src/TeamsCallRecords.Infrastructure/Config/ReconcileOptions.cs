namespace TeamsCallRecords.Infrastructure.Config;

/// <summary>Section "Reconcile" (TRP 3.13.6).</summary>
public sealed class ReconcileOptions
{
    public int IntervalMinutes { get; set; } = 15;
    public int LookbackHours { get; set; } = 24;
}
