using System.Globalization;

namespace TeamsCallRecords.Host.Tests;

/// <summary>Synthetic callRecord built from the public Microsoft Graph resource structure. No real data.</summary>
public static class Samples
{
    private static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    public static string CallRecord(string id, DateTimeOffset start, int seconds = 61, long version = 3)
    {
        var s = Iso(start);
        var e = Iso(start.AddSeconds(seconds));
        const string ep = "\"@odata.type\":\"#microsoft.graph.callRecords.participantEndpoint\"";
        return "{\"id\":\"" + id + "\",\"version\":" + version + ",\"type\":\"peerToPeer\",\"modalities\":[\"audio\"]," +
               "\"startDateTime\":\"" + s + "\",\"endDateTime\":\"" + e + "\",\"sessions\":[{" +
               "\"id\":\"" + id + "-s1\",\"modalities\":[\"audio\"],\"startDateTime\":\"" + s + "\",\"endDateTime\":\"" + e + "\"," +
               "\"caller\":{" + ep + ",\"identity\":{\"user\":{\"id\":\"user-a\"}}}," +
               "\"callee\":{" + ep + ",\"identity\":{\"user\":{\"id\":\"user-b\"}}}," +
               "\"segments\":[{\"id\":\"seg-1\",\"startDateTime\":\"" + s + "\",\"endDateTime\":\"" + e + "\"}]}]}";
    }

    public static string Notification(string callId, string clientState = TcriFactory.ClientState, string tenant = TcriFactory.Tenant) =>
        "{\"value\":[{\"subscriptionId\":\"sub-1\",\"changeType\":\"created\",\"clientState\":\"" + clientState + "\"," +
        "\"tenantId\":\"" + tenant + "\",\"resource\":\"communications/callRecords/" + callId + "\"," +
        "\"resourceData\":{\"@odata.type\":\"#microsoft.graph.callRecords.callRecord\",\"id\":\"" + callId + "\"}}]}";

    public static string Range(DateTimeOffset t, int beforeMinutes = 60, int afterMinutes = 60) =>
        "from=" + Uri.EscapeDataString(Iso(t.AddMinutes(-beforeMinutes))) + "&to=" + Uri.EscapeDataString(Iso(t.AddMinutes(afterMinutes)));

    public static string RangeBody(DateTimeOffset t) =>
        "{\"from_utc\":\"" + Iso(t.AddMinutes(-60)) + "\",\"to_utc\":\"" + Iso(t.AddMinutes(60)) + "\",\"force\":true}";
}
