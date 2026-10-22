namespace TeamsCallRecords.Core;

public enum CallType { Unknown, OneToOne, Group }

public enum CdrStatus { Completed, Failed, Unknown }

public enum AggregationStatus { Complete, Incomplete }

/// <summary>Normalized party: type is user, application, phone, service or unknown.</summary>
public sealed record PartyRef(string Type, string Id);

/// <summary>One CDR per session of a call record (TZ v1.0, F6 rule 1).</summary>
public sealed record Cdr
{
    public int SchemaVersion { get; init; } = 1;
    public string EventType { get; init; } = "call";
    public required string TenantId { get; init; }
    public required string CallId { get; init; }
    public required string SessionId { get; init; }
    public required long SourceVersion { get; init; }
    public required CallType CallType { get; init; }
    public PartyRef? Caller { get; init; }
    public PartyRef? Callee { get; init; }
    public required DateTimeOffset StartUtc { get; init; }
    public required DateTimeOffset EndUtc { get; init; }
    public required long DurationSeconds { get; init; }
    public required IReadOnlyList<string> Modalities { get; init; }
    public required CdrStatus Status { get; init; }
    public string? TerminationReason { get; init; }
    public required int SegmentCount { get; init; }
    public required AggregationStatus AggregationStatus { get; init; }
    public required string SourceHash { get; init; }
    public string? ExtensionsJson { get; init; }
}

public sealed record Rejection(string? CallId, string? SessionId, string Code, string Message);

public static class RejectionCodes
{
    public const string BadJson = "CALLRECORD_BAD_JSON";
    public const string NoCallId = "CALLRECORD_NO_ID";
    public const string NoVersion = "CALLRECORD_NO_VERSION";
    public const string SessionNoId = "SESSION_NO_ID";
    public const string SessionBadDate = "SESSION_BAD_DATE";
    public const string SessionEndBeforeStart = "SESSION_END_BEFORE_START";
}

public sealed record ConversionResult
{
    public string? CallId { get; init; }
    public long? SourceVersion { get; init; }
    public IReadOnlyList<Cdr> Cdrs { get; init; } = Array.Empty<Cdr>();
    public IReadOnlyList<Rejection> Rejections { get; init; } = Array.Empty<Rejection>();
    /// <summary>True only when the session set is complete and valid, so it may replace stored CDRs.</summary>
    public bool IsComplete { get; init; }
    public IReadOnlyList<string> IncompleteReasons { get; init; } = Array.Empty<string>();
    public string? SessionSetHash { get; init; }
}
