using System.Globalization;

namespace TeamsCallRecords.Infrastructure.Data;

/// <summary>
/// Database timestamp format: ISO 8601 UTC, exactly seven fractional digits and a Z suffix,
/// e.g. 2026-10-20T09:00:00.5000000Z. Lexicographic order equals chronological order.
/// </summary>
public static class UtcText
{
    private const string Pattern = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    public static string Format(DateTimeOffset value) =>
        value.UtcDateTime.ToString(Pattern, CultureInfo.InvariantCulture);

    public static DateTimeOffset Parse(string text) =>
        DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    public static string Now(TimeProvider clock) => Format(clock.GetUtcNow());
}
