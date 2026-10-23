using System.Text.Json;
using TeamsCallRecords.Core;
using TeamsCallRecords.Infrastructure.Data.Entities;

namespace TeamsCallRecords.Infrastructure.Data;

/// <summary>Conversion between the Core <see cref="Cdr"/> and the table row <see cref="CdrRow"/>.</summary>
public static class CdrMapping
{
    /// <summary>Cdr to row. cdr_seq is not populated; created_utc = updated_utc = nowText.</summary>
    public static CdrRow ToRow(Cdr cdr, string nowText) => new()
    {
        SchemaVersion = cdr.SchemaVersion,
        EventType = cdr.EventType,
        TenantId = cdr.TenantId,
        CallId = cdr.CallId,
        SessionId = cdr.SessionId,
        SourceVersion = cdr.SourceVersion,
        CallType = CallTypeText(cdr.CallType),
        CallerType = cdr.Caller?.Type,
        CallerId = cdr.Caller?.Id,
        CalleeType = cdr.Callee?.Type,
        CalleeId = cdr.Callee?.Id,
        StartUtc = UtcText.Format(cdr.StartUtc),
        EndUtc = UtcText.Format(cdr.EndUtc),
        DurationS = cdr.DurationSeconds,
        Modalities = JsonSerializer.Serialize(cdr.Modalities),
        Status = StatusText(cdr.Status),
        TerminationReason = cdr.TerminationReason,
        SegmentCount = cdr.SegmentCount,
        AggregationStatus = AggregationText(cdr.AggregationStatus),
        SourceHash = cdr.SourceHash,
        Extensions = cdr.ExtensionsJson,
        CreatedUtc = nowText,
        UpdatedUtc = nowText,
    };

    /// <summary>Row to Cdr. ToCdr(ToRow(c)) equals c for all properties.</summary>
    public static Cdr ToCdr(CdrRow row) => new()
    {
        SchemaVersion = row.SchemaVersion,
        EventType = row.EventType,
        TenantId = row.TenantId,
        CallId = row.CallId,
        SessionId = row.SessionId,
        SourceVersion = row.SourceVersion,
        CallType = ParseCallType(row.CallType),
        Caller = Party(row.CallerType, row.CallerId),
        Callee = Party(row.CalleeType, row.CalleeId),
        StartUtc = UtcText.Parse(row.StartUtc),
        EndUtc = UtcText.Parse(row.EndUtc),
        DurationSeconds = row.DurationS,
        Modalities = JsonSerializer.Deserialize<List<string>>(row.Modalities) ?? new List<string>(),
        Status = ParseStatus(row.Status),
        TerminationReason = row.TerminationReason,
        SegmentCount = row.SegmentCount,
        AggregationStatus = ParseAggregation(row.AggregationStatus),
        SourceHash = row.SourceHash,
        ExtensionsJson = row.Extensions,
    };

    /// <summary>Replacement: copies every column except cdr_seq and created_utc; updated_utc = nowText.</summary>
    public static void CopyOnUpdate(CdrRow target, CdrRow source, string nowText)
    {
        target.SchemaVersion = source.SchemaVersion;
        target.EventType = source.EventType;
        target.TenantId = source.TenantId;
        target.CallId = source.CallId;
        target.SessionId = source.SessionId;
        target.SourceVersion = source.SourceVersion;
        target.CallType = source.CallType;
        target.CallerType = source.CallerType;
        target.CallerId = source.CallerId;
        target.CalleeType = source.CalleeType;
        target.CalleeId = source.CalleeId;
        target.StartUtc = source.StartUtc;
        target.EndUtc = source.EndUtc;
        target.DurationS = source.DurationS;
        target.Modalities = source.Modalities;
        target.Status = source.Status;
        target.TerminationReason = source.TerminationReason;
        target.SegmentCount = source.SegmentCount;
        target.AggregationStatus = source.AggregationStatus;
        target.SourceHash = source.SourceHash;
        target.Extensions = source.Extensions;
        target.UpdatedUtc = nowText;
    }

    private static PartyRef? Party(string? type, string? id) =>
        type is null || id is null ? null : new PartyRef(type, id);

    private static string CallTypeText(CallType v) => v switch
    {
        CallType.OneToOne => "oneToOne",
        CallType.Group => "group",
        CallType.Unknown => "unknown",
        _ => throw new ArgumentOutOfRangeException(nameof(v), v, null),
    };

    private static CallType ParseCallType(string s) => s switch
    {
        "oneToOne" => CallType.OneToOne,
        "group" => CallType.Group,
        "unknown" => CallType.Unknown,
        _ => throw new FormatException("call_type: " + s),
    };

    private static string StatusText(CdrStatus v) => v switch
    {
        CdrStatus.Completed => "completed",
        CdrStatus.Failed => "failed",
        CdrStatus.Unknown => "unknown",
        _ => throw new ArgumentOutOfRangeException(nameof(v), v, null),
    };

    private static CdrStatus ParseStatus(string s) => s switch
    {
        "completed" => CdrStatus.Completed,
        "failed" => CdrStatus.Failed,
        "unknown" => CdrStatus.Unknown,
        _ => throw new FormatException("status: " + s),
    };

    private static string AggregationText(AggregationStatus v) => v switch
    {
        AggregationStatus.Complete => "complete",
        AggregationStatus.Incomplete => "incomplete",
        _ => throw new ArgumentOutOfRangeException(nameof(v), v, null),
    };

    private static AggregationStatus ParseAggregation(string s) => s switch
    {
        "complete" => AggregationStatus.Complete,
        "incomplete" => AggregationStatus.Incomplete,
        _ => throw new FormatException("aggregation_status: " + s),
    };
}
