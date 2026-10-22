using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace TeamsCallRecords.Core;

/// <summary>
/// Canonical JSON and SHA-256 hashing of session content (Sketch design v0.3, section 3).
/// Object properties are sorted ordinally, array order is preserved, values are written as in the source.
/// Transport annotations are excluded by an explicit list of property-name suffixes.
/// </summary>
public static class Canonical
{
    public static readonly IReadOnlyList<string> ExcludedSuffixes =
        new[] { "@odata.context", "@odata.nextLink", "@odata.count" };

    public static string ToCanonicalJson(JsonElement element)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            Write(w, element);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static string Sha256Hex(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    public static string HashOf(JsonElement element) => Sha256Hex(ToCanonicalJson(element));

    /// <summary>Hash of the whole session set: ordered (session id, session hash) pairs, so the id composition counts too.</summary>
    public static string SessionSetHash(IEnumerable<(string Id, string Hash)> sessions)
    {
        var sb = new StringBuilder();
        foreach (var (id, hash) in sessions.OrderBy(s => s.Id, StringComparer.Ordinal))
            sb.Append(id.Length).Append(':').Append(id).Append('=').Append(hash).Append('\n');
        return Sha256Hex(sb.ToString());
    }

    private static bool IsExcluded(string name)
    {
        foreach (var s in ExcludedSuffixes)
            if (name.EndsWith(s, StringComparison.Ordinal)) return true;
        return false;
    }

    private static void Write(Utf8JsonWriter w, JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                w.WriteStartObject();
                foreach (var p in e.EnumerateObject().Where(p => !IsExcluded(p.Name)).OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    w.WritePropertyName(p.Name);
                    Write(w, p.Value);
                }
                w.WriteEndObject();
                break;
            case JsonValueKind.Array:
                w.WriteStartArray();
                foreach (var item in e.EnumerateArray()) Write(w, item);
                w.WriteEndArray();
                break;
            case JsonValueKind.String:
                w.WriteStringValue(e.GetString());
                break;
            case JsonValueKind.Number:
                w.WriteRawValue(e.GetRawText());
                break;
            case JsonValueKind.True: w.WriteBooleanValue(true); break;
            case JsonValueKind.False: w.WriteBooleanValue(false); break;
            default: w.WriteNullValue(); break;
        }
    }
}
