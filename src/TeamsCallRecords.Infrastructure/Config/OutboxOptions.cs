using System.Globalization;

namespace TeamsCallRecords.Infrastructure.Config;

/// <summary>Section "Outbox" (TRP 3.16.1, 3.18).</summary>
public sealed class OutboxOptions
{
    public int MaxAttempts { get; set; } = 8;
    public int LeaseSeconds { get; set; } = 120;
    public string BackoffSeconds { get; set; } = "30,120,600,1800,3600";
    public int MaxAgeHours { get; set; } = 24;

    public IReadOnlyList<int> BackoffScheduleSeconds()
    {
        var parts = (BackoffSeconds ?? "").Split(',', StringSplitOptions.TrimEntries);
        var result = new List<int>(parts.Length);
        foreach (var p in parts)
        {
            if (!int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 1)
                throw new FormatException("Outbox:BackoffSeconds must be positive comma-separated integers");
            result.Add(n);
        }
        return result;
    }

    /// <summary>Seconds at index min(attempt-1, n-1).</summary>
    public TimeSpan BackoffFor(int attempt)
    {
        if (attempt < 1) throw new ArgumentOutOfRangeException(nameof(attempt));
        var s = BackoffScheduleSeconds();
        return TimeSpan.FromSeconds(s[Math.Min(attempt - 1, s.Count - 1)]);
    }

    public IEnumerable<string> Validate()
    {
        if (MaxAttempts < 1 || MaxAttempts > 100) yield return "Outbox:MaxAttempts must be 1..100";
        if (LeaseSeconds < 10 || LeaseSeconds > 3600) yield return "Outbox:LeaseSeconds must be 10..3600";
        if (MaxAgeHours < 1 || MaxAgeHours > 720) yield return "Outbox:MaxAgeHours must be 1..720";
        string? error = null;
        try { BackoffScheduleSeconds(); } catch (FormatException e) { error = e.Message; }
        if (error != null) yield return error;
    }
}
