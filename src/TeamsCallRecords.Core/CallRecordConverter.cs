using System.Globalization;
using System.Text.Json;

namespace TeamsCallRecords.Core;

/// <summary>
/// F6: Microsoft Graph callRecord JSON (with sessions and segments expanded) to CDRs.
/// Pure function: no network, no database. Rules follow TZ v1.0, section 2.
/// </summary>
public static class CallRecordConverter
{
    public static ConversionResult Convert(string callRecordJson, string tenantId)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(callRecordJson); }
        catch (JsonException ex)
        {
            return Bad(null, RejectionCodes.BadJson, ex.Message);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Bad(null, RejectionCodes.BadJson, "Root is not an object.");

            var callId = Str(root, "id");
            if (string.IsNullOrEmpty(callId))
                return Bad(null, RejectionCodes.NoCallId, "callRecord has no id.");

            if (!root.TryGetProperty("version", out var vEl) || vEl.ValueKind != JsonValueKind.Number || !vEl.TryGetInt64(out var version))
                return Bad(callId, RejectionCodes.NoVersion, "callRecord has no numeric version.");

            var callType = Str(root, "type") switch
            {
                "peerToPeer" => CallType.OneToOne,
                "groupCall" => CallType.Group,
                _ => CallType.Unknown
            };
            var recordModalities = Strings(root, "modalities");

            var rejections = new List<Rejection>();
            var reasons = new List<string>();
            var cdrs = new List<Cdr>();
            var idHash = new List<(string, string)>();

            if (!root.TryGetProperty("sessions", out var sessions) || sessions.ValueKind != JsonValueKind.Array || sessions.GetArrayLength() == 0)
            {
                reasons.Add("NO_SESSIONS");
                return Result(callId, version, cdrs, rejections, reasons, idHash);
            }

            var sessionList = sessions.EnumerateArray().ToList();

            // First pass: hashes and ids for the whole set, so the set hash reflects every session present.
            var parsed = new List<(JsonElement El, string? Id, string Hash)>();
            foreach (var s in sessionList)
            {
                var sid = s.ValueKind == JsonValueKind.Object ? Str(s, "id") : null;
                parsed.Add((s, sid, Canonical.HashOf(s)));
                if (!string.IsNullOrEmpty(sid)) idHash.Add((sid, parsed[^1].Hash));
            }

            foreach (var (s, sid, hash) in parsed)
            {
                if (string.IsNullOrEmpty(sid))
                {
                    rejections.Add(new Rejection(callId, null, RejectionCodes.SessionNoId, "Session has no id."));
                    reasons.Add("SESSION_INVALID");
                    continue;
                }

                var startState = ReadDate(s, "startDateTime", out var start);
                var endState = ReadDate(s, "endDateTime", out var end);
                if (startState == DateState.Bad || endState == DateState.Bad)
                {
                    rejections.Add(new Rejection(callId, sid, RejectionCodes.SessionBadDate, "Unparsable session date."));
                    reasons.Add("SESSION_INVALID");
                    continue;
                }
                if (startState == DateState.Missing || endState == DateState.Missing)
                {
                    // Data may still arrive in a later version: keep stored CDRs, wait.
                    reasons.Add("SESSION_DATES_MISSING");
                    continue;
                }
                if (end < start)
                {
                    rejections.Add(new Rejection(callId, sid, RejectionCodes.SessionEndBeforeStart, "Session ends before it starts."));
                    reasons.Add("SESSION_INVALID");
                    continue;
                }

                var segments = s.TryGetProperty("segments", out var segEl) && segEl.ValueKind == JsonValueKind.Array
                    ? segEl.EnumerateArray().ToList()
                    : new List<JsonElement>();

                var failure = FailureOf(s) ?? segments.Select(FailureOf).FirstOrDefault(f => f is not null);
                var modalities = Strings(s, "modalities");
                if (modalities.Count == 0) modalities = recordModalities;

                cdrs.Add(new Cdr
                {
                    TenantId = tenantId,
                    CallId = callId,
                    SessionId = sid,
                    SourceVersion = version,
                    CallType = callType,
                    Caller = Party(s, "caller"),
                    Callee = Party(s, "callee"),
                    StartUtc = start,
                    EndUtc = end,
                    DurationSeconds = VersionRules.RoundDurationSeconds(start, end),
                    Modalities = modalities,
                    Status = failure is not null ? CdrStatus.Failed : CdrStatus.Completed,
                    TerminationReason = failure,
                    SegmentCount = segments.Count,
                    AggregationStatus = AggregationStatus.Complete, // fixed below once the whole set is known
                    SourceHash = hash,
                    ExtensionsJson = SegmentsJson(segments)
                });
            }

            return Result(callId, version, cdrs, rejections, reasons, idHash);
        }
    }

    private static ConversionResult Result(string callId, long version, List<Cdr> cdrs, List<Rejection> rejections,
        List<string> reasons, List<(string, string)> idHash)
    {
        var complete = reasons.Count == 0;
        var status = complete ? AggregationStatus.Complete : AggregationStatus.Incomplete;
        return new ConversionResult
        {
            CallId = callId,
            SourceVersion = version,
            Cdrs = cdrs.Select(c => c with { AggregationStatus = status }).ToList(),
            Rejections = rejections,
            IsComplete = complete,
            IncompleteReasons = reasons.Distinct().ToList(),
            SessionSetHash = Canonical.SessionSetHash(idHash)
        };
    }

    private static ConversionResult Bad(string? callId, string code, string message) => new()
    {
        CallId = callId,
        Rejections = new[] { new Rejection(callId, null, code, message) },
        IsComplete = false,
        IncompleteReasons = new[] { code }
    };

    private enum DateState { Ok, Missing, Bad }

    private static DateState ReadDate(JsonElement obj, string name, out DateTimeOffset value)
    {
        value = default;
        if (!obj.TryGetProperty(name, out var el) || el.ValueKind == JsonValueKind.Null) return DateState.Missing;
        if (el.ValueKind != JsonValueKind.String) return DateState.Bad;
        var text = el.GetString();
        if (string.IsNullOrWhiteSpace(text)) return DateState.Missing;
        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value))
            return DateState.Bad;
        return DateState.Ok;
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() : null;

    private static IReadOnlyList<string> Strings(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Array
            ? el.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
            : new List<string>();

    private static string? FailureOf(JsonElement obj)
    {
        if (!obj.TryGetProperty("failureInfo", out var f) || f.ValueKind != JsonValueKind.Object) return null;
        var stage = Str(f, "stage") ?? "unknown";
        var reason = Str(f, "reason") ?? "unknown";
        return $"{stage}:{reason}";
    }

    /// <summary>Caller or callee: absent endpoint gives null, an endpoint without a known identity gives type "unknown".</summary>
    private static PartyRef? Party(JsonElement session, string name)
    {
        if (!session.TryGetProperty(name, out var ep) || ep.ValueKind != JsonValueKind.Object) return null;
        if (ep.TryGetProperty("identity", out var identity) && identity.ValueKind == JsonValueKind.Object)
        {
            foreach (var kind in new[] { "user", "application", "phone" })
                if (identity.TryGetProperty(kind, out var who) && who.ValueKind == JsonValueKind.Object && Str(who, "id") is { Length: > 0 } id)
                    return new PartyRef(kind, id);
        }
        var odataType = Str(ep, "@odata.type");
        return odataType is not null && odataType.Contains("serviceEndpoint", StringComparison.OrdinalIgnoreCase)
            ? new PartyRef("service", "")
            : new PartyRef("unknown", "");
    }

    private static string? SegmentsJson(List<JsonElement> segments)
    {
        if (segments.Count == 0) return null;
        var list = segments.Select(s => new
        {
            id = Str(s, "id"),
            start = Str(s, "startDateTime"),
            end = Str(s, "endDateTime"),
            failure = FailureOf(s)
        });
        return JsonSerializer.Serialize(new { segments = list });
    }
}
