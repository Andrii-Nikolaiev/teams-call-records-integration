using System.Text;

namespace TeamsCallRecords.Infrastructure.Tests.Support;

/// <summary>Synthetic callRecords built from the public Microsoft Graph resource structure. No real data.</summary>
public static class GraphSamples
{
    public const string Start = "2026-10-20T17:50:00Z";
    public const string End = "2026-10-20T17:51:01Z";

    private static string Q(string? s) => s is null ? "null" : "\"" + s + "\"";

    private static string Endpoint(string name, string userId) =>
        userId == "" ? "" :
        "\"" + name + "\":{\"@odata.type\":\"#microsoft.graph.callRecords.participantEndpoint\"," +
        "\"identity\":{\"user\":{\"id\":\"" + userId + "\"}}},";

    public static string Session(string id, string? start = Start, string? end = End,
        string caller = "user-a", string callee = "user-b", string? failure = null)
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
        sb.Append("\"segments\":[{\"id\":\"seg-").Append(id).Append("\",\"startDateTime\":").Append(Q(start))
          .Append(",\"endDateTime\":").Append(Q(end)).Append("}]}");
        return sb.ToString();
    }

    /// <summary>Same session, but the caller is an application instead of a user.</summary>
    public static string AppCallerSession(string id) =>
        Session(id).Replace("\"caller\":{\"@odata.type\":\"#microsoft.graph.callRecords.participantEndpoint\",\"identity\":{\"user\":",
            "\"caller\":{\"@odata.type\":\"#microsoft.graph.callRecords.participantEndpoint\",\"identity\":{\"application\":");

    public static string Record(string sessions, long version = 3, string type = "peerToPeer", string id = "call-1") =>
        "{\"id\":\"" + id + "\",\"version\":" + version + ",\"type\":\"" + type + "\",\"modalities\":[\"audio\"]," +
        "\"startDateTime\":\"" + Start + "\",\"endDateTime\":\"" + End + "\",\"sessions\":[" + sessions + "]}";

    public static string Simple(long version = 3, string id = "call-1") => Record(Session("s1"), version, id: id);
}
