using System.Diagnostics.CodeAnalysis;

namespace TeamsCallRecords.Host.Config;

/// <summary>Section "Bot" (TRP 3.24): settings of the Teams bot commands.</summary>
public sealed class BotOptions
{
    /// <summary>Time zone that defines "today" for the bot commands (IANA id).</summary>
    public string TimeZoneId { get; set; } = "Europe/Kyiv";

    public IEnumerable<string> Validate()
    {
        if (string.IsNullOrWhiteSpace(TimeZoneId))
        {
            yield return "Bot:TimeZoneId must not be empty";
            yield break;
        }
        if (!TryResolveTimeZone(out _))
            yield return $"Bot:TimeZoneId '{TimeZoneId}' is not a known time zone";
    }

    public TimeZoneInfo ResolveTimeZone() => FindTimeZone(TimeZoneId);

    /// <summary>
    /// Finds a time zone by its IANA id. Some systems (for example Windows with an older ICU/tzdata) do not know the
    /// current id Europe/Kyiv, so the older id and the Windows id of the same zone are tried next.
    /// </summary>
    public static TimeZoneInfo FindTimeZone(string id)
    {
        var candidates = id.Equals("Europe/Kyiv", StringComparison.OrdinalIgnoreCase)
            ? new[] { id, "Europe/Kiev", "FLE Standard Time" }
            : new[] { id };
        foreach (var candidate in candidates)
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(candidate); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        throw new TimeZoneNotFoundException($"The time zone ID '{id}' was not found on the local computer.");
    }

    public bool TryResolveTimeZone([NotNullWhen(true)] out TimeZoneInfo? timeZone)
    {
        try
        {
            timeZone = FindTimeZone(TimeZoneId);
            return true;
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            timeZone = null;
            return false;
        }
    }
}
