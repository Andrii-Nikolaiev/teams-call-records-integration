namespace TeamsCallRecords.Core;

public enum VersionAction
{
    /// <summary>Incoming version is higher (or nothing applied yet): replace the CDR set.</summary>
    Apply,
    /// <summary>Incoming version is lower than the applied one: ignore, log the event.</summary>
    IgnoreLower,
    /// <summary>Same version, same content: repeat, close the item.</summary>
    DuplicateSame,
    /// <summary>Same version, different content: do not replace, write to audit_log.</summary>
    Discrepancy
}

public static class VersionRules
{
    public static VersionAction Decide(long? appliedVersion, string? appliedSetHash, long incomingVersion, string incomingSetHash)
    {
        if (appliedVersion is null) return VersionAction.Apply;
        if (incomingVersion < appliedVersion) return VersionAction.IgnoreLower;
        if (incomingVersion > appliedVersion) return VersionAction.Apply;
        return string.Equals(appliedSetHash, incomingSetHash, StringComparison.Ordinal)
            ? VersionAction.DuplicateSame
            : VersionAction.Discrepancy;
    }

    /// <summary>Whole seconds, nearest, a half second rounds up (TZ v1.0, F6 rule 3). Integer arithmetic on ticks.</summary>
    public static long RoundDurationSeconds(DateTimeOffset start, DateTimeOffset end)
    {
        var ticks = (end - start).Ticks;
        const long second = TimeSpan.TicksPerSecond;
        return ticks >= 0 ? (ticks + second / 2) / second : -((-ticks + second / 2) / second);
    }
}
