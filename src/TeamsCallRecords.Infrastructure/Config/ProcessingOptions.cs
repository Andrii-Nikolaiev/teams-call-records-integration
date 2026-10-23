using System.Globalization;

namespace TeamsCallRecords.Infrastructure.Config;

/// <summary>Section "Processing" (TRP 3.13.2).</summary>
public sealed class ProcessingOptions
{
    public int Workers { get; set; } = 2;
    public int LeaseSeconds { get; set; } = 120;
    public string RetrySchedule { get; set; } = "1,5,15,60";
    public int PendingLimitHours { get; set; } = 4;
    public int SuspendedRecheckHours { get; set; } = 24;
    public int MaxAttempts { get; set; } = 10;

    /// <summary>Positive comma-separated integers; spaces allowed.</summary>
    public IReadOnlyList<int> RetryScheduleMinutes()
    {
        var parts = (RetrySchedule ?? "").Split(',', StringSplitOptions.TrimEntries);
        var result = new List<int>(parts.Length);
        foreach (var p in parts)
        {
            if (!int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 1)
                throw new FormatException("Processing:RetrySchedule must be positive comma-separated integers");
            result.Add(n);
        }
        return result;
    }

    /// <summary>Minutes at index min(attempt-1, n-1); the last interval repeats.</summary>
    public TimeSpan RetryDelayFor(int attempt)
    {
        if (attempt < 1) throw new ArgumentOutOfRangeException(nameof(attempt));
        var minutes = RetryScheduleMinutes();
        return TimeSpan.FromMinutes(minutes[Math.Min(attempt - 1, minutes.Count - 1)]);
    }

    public IEnumerable<string> Validate()
    {
        if (Workers < 1 || Workers > 16) yield return "Processing:Workers must be 1..16";
        if (LeaseSeconds < 10 || LeaseSeconds > 3600) yield return "Processing:LeaseSeconds must be 10..3600";
        if (PendingLimitHours < 1 || PendingLimitHours > 72) yield return "Processing:PendingLimitHours must be 1..72";
        if (SuspendedRecheckHours < 1 || SuspendedRecheckHours > 168) yield return "Processing:SuspendedRecheckHours must be 1..168";
        if (MaxAttempts < 1 || MaxAttempts > 100) yield return "Processing:MaxAttempts must be 1..100";
        string? scheduleError = null;
        try { RetryScheduleMinutes(); } catch (FormatException e) { scheduleError = e.Message; }
        if (scheduleError != null) yield return scheduleError;
    }
}
