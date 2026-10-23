namespace TeamsCallRecords.Infrastructure.Config;

/// <summary>Section "Reconcile" (TRP 3.13.6).</summary>
public sealed class ReconcileOptions
{
    public int IntervalMinutes { get; set; } = 15;
    public int LookbackHours { get; set; } = 24;

    public IEnumerable<string> Validate()
    {
        if (IntervalMinutes < 1 || IntervalMinutes > 1440) yield return "Reconcile:IntervalMinutes must be 1..1440";
        if (LookbackHours < 1 || LookbackHours > 720) yield return "Reconcile:LookbackHours must be 1..720";
    }
}
