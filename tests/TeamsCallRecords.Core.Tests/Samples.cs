using System.Text;

namespace TeamsCallRecords.Core.Tests;

/// <summary>Synthetic callRecords built from the public Microsoft Graph resource structure. No real data.</summary>
internal static class Samples
{
    public const string Tenant = "tenant-test";

    private static string Q(string? s) => s is null ? "null" : "\"" + s + "\"";

    private static string Endpoint(string name, string userId) =>
        userId == "" ? "" :
        "\"" + name + "\":{\"@odata.type\":\"#microsoft.graph.callRecords.participantEndpoint\"," +
        "\"identity\":{\"user\":{\"id\":\"" + userId + "\"}}},";

    public static string Session(string id, string? start = "2026-01-01T10:00:00Z", string? end = "2026-01-01T10:01:00Z",
        string caller = "user-a", string callee = "user-b", string? failure = null, string segments = "")
    {
        var sb = new StringBuilder();
        sb.Append("{\"id\":\"").Append(id).Append("\",\"modalities\":[\"audio\"],");
        sb.Append("\"startDateTime\":").Append(Q(start)).Append(',');
        sb.Append("\"endDateTime\":").Append(Q(end)).Append(',');
        sb.Append(Endpoint("caller", caller)).Append(Endpoint("callee", callee));
        if (failure is not null)
        {
            var parts = failure.Split(':');
            sb.Append("\"failureInfo\":{\"stage\":\"").Append(parts[0]).Append("\",\"reason\":\"").Append(parts[1]).Append("\"},");
        }
        sb.Append("\"segments\":[").Append(segments == "" ? Segment("seg-" + id) : segments).Append("]}");
        return sb.ToString();
    }

    public static string Segment(string id, string start = "2026-01-01T10:00:00Z", string end = "2026-01-01T10:01:00Z") =>
        "{\"id\":\"" + id + "\",\"startDateTime\":\"" + start + "\",\"endDateTime\":\"" + end + "\"," +
        "\"media\":[{\"label\":\"main-audio\",\"streamDirection\":\"callerToCallee\"}]}";

    public static string Record(string sessions, long version = 3, string type = "peerToPeer", string id = "call-1") =>
        "{\"id\":\"" + id + "\",\"version\":" + version + ",\"type\":\"" + type + "\",\"modalities\":[\"audio\"]," +
        "\"startDateTime\":\"2026-01-01T10:00:00Z\",\"endDateTime\":\"2026-01-01T10:01:00Z\",\"sessions\":[" + sessions + "]}";
}
