namespace TeamsCallRecords.Host.Web;

/// <summary>Compares a subscribed resource with the resource of a notification.</summary>
public static class ResourceMatch
{
    public static string Normalize(string resource) => resource.Trim().Trim('/');

    /// <summary>
    /// True when the notification resource is the subscribed resource or lies under it
    /// ("communications/callRecords" covers "communications/callRecords/{id}"). Case-insensitive; leading and trailing '/' are ignored.
    /// </summary>
    public static bool Covers(string subscribed, string notified)
    {
        var s = Normalize(subscribed);
        var n = Normalize(notified);
        if (s.Length == 0) return false;
        return string.Equals(s, n, StringComparison.OrdinalIgnoreCase)
               || (n.Length > s.Length && n.StartsWith(s + "/", StringComparison.OrdinalIgnoreCase));
    }
}
