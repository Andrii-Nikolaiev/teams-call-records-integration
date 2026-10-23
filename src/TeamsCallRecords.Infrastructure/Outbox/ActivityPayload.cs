using System.Text.Json;
using TeamsCallRecords.Core;
using TeamsCallRecords.Infrastructure.Data;

namespace TeamsCallRecords.Infrastructure.Outbox;

/// <summary>Payload of an outbound activity notification (TRP 3.16.1).</summary>
public sealed record ActivityPayload(string ActivityType, string CallType, string Status, long DurationSeconds, string StartUtc)
{
    public const string CallCompleted = "callCompleted";

    public static ActivityPayload Create(Cdr cdr)
    {
        var row = CdrMapping.ToRow(cdr, "");   // reuse the canonical enum-to-text mapping
        return new ActivityPayload(CallCompleted, row.CallType, row.Status, cdr.DurationSeconds, UtcText.Format(cdr.StartUtc));
    }

    public string ToJson()
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("activity_type", ActivityType);
            w.WriteString("call_type", CallType);
            w.WriteString("status", Status);
            w.WriteNumber("duration_s", DurationSeconds);
            w.WriteString("start_utc", StartUtc);
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    public static ActivityPayload Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) throw new FormatException("payload is not an object");
            return new ActivityPayload(
                r.GetProperty("activity_type").GetString() ?? throw new FormatException("activity_type"),
                r.GetProperty("call_type").GetString() ?? throw new FormatException("call_type"),
                r.GetProperty("status").GetString() ?? throw new FormatException("status"),
                r.GetProperty("duration_s").GetInt64(),
                r.GetProperty("start_utc").GetString() ?? throw new FormatException("start_utc"));
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new FormatException("invalid activity payload", e);
        }
    }
}
