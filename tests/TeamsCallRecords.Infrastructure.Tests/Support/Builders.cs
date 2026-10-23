using TeamsCallRecords.Core;

namespace TeamsCallRecords.Infrastructure.Tests.Support;

/// <summary>Builders for test data. No real identifiers.</summary>
public static class Builders
{
    public static Cdr Cdr(
        string callId = "call-1",
        string sessionId = "sess-1",
        long version = 3,
        CallType callType = CallType.OneToOne,
        PartyRef? caller = null,
        PartyRef? callee = null,
        DateTimeOffset? start = null,
        long durationSeconds = 60,
        string[]? modalities = null,
        CdrStatus status = CdrStatus.Completed,
        string? terminationReason = null,
        int segments = 1,
        AggregationStatus aggregation = AggregationStatus.Complete,
        string? extensionsJson = null)
    {
        var s = start ?? new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);
        return new Cdr
        {
            TenantId = TestDb.Tenant,
            CallId = callId,
            SessionId = sessionId,
            SourceVersion = version,
            CallType = callType,
            Caller = caller,
            Callee = callee,
            StartUtc = s,
            EndUtc = s.AddSeconds(durationSeconds),
            DurationSeconds = durationSeconds,
            Modalities = modalities ?? new[] { "audio" },
            Status = status,
            TerminationReason = terminationReason,
            SegmentCount = segments,
            AggregationStatus = aggregation,
            SourceHash = "hash-" + callId + "-" + sessionId,
            ExtensionsJson = extensionsJson,
        };
    }

    public static IEnumerable<Cdr> RoundTripSamples()
    {
        var a = new PartyRef("user", "user-a");
        var b = new PartyRef("user", "user-b");
        yield return Cdr(caller: a, callee: b);
        yield return Cdr("call-2", "sess-2", callType: CallType.Group, caller: a, modalities: new[] { "audio", "video" }, segments: 4);
        yield return Cdr("call-3", "sess-3", callType: CallType.Unknown, status: CdrStatus.Unknown);
        yield return Cdr("call-4", "sess-4", caller: a, callee: new PartyRef("phone", "+000000000"),
            status: CdrStatus.Failed, terminationReason: "setup:Failed");
        yield return Cdr("call-5", "sess-5", caller: a, callee: b, aggregation: AggregationStatus.Incomplete,
            extensionsJson: "{\"segments\":[{\"id\":\"seg-1\"}]}");
        yield return Cdr("call-6", "sess-6", caller: a, callee: b,
            start: new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero).AddTicks(1234567), durationSeconds: 0);
    }

    public static void AssertEqual(Cdr expected, Cdr actual)
    {
        Assert.Equal(expected.SchemaVersion, actual.SchemaVersion);
        Assert.Equal(expected.EventType, actual.EventType);
        Assert.Equal(expected.TenantId, actual.TenantId);
        Assert.Equal(expected.CallId, actual.CallId);
        Assert.Equal(expected.SessionId, actual.SessionId);
        Assert.Equal(expected.SourceVersion, actual.SourceVersion);
        Assert.Equal(expected.CallType, actual.CallType);
        Assert.Equal(expected.Caller, actual.Caller);
        Assert.Equal(expected.Callee, actual.Callee);
        Assert.Equal(expected.StartUtc.UtcTicks, actual.StartUtc.UtcTicks);
        Assert.Equal(expected.EndUtc.UtcTicks, actual.EndUtc.UtcTicks);
        Assert.Equal(TimeSpan.Zero, actual.StartUtc.Offset);
        Assert.Equal(expected.DurationSeconds, actual.DurationSeconds);
        Assert.Equal(expected.Modalities, actual.Modalities);
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.TerminationReason, actual.TerminationReason);
        Assert.Equal(expected.SegmentCount, actual.SegmentCount);
        Assert.Equal(expected.AggregationStatus, actual.AggregationStatus);
        Assert.Equal(expected.SourceHash, actual.SourceHash);
        Assert.Equal(expected.ExtensionsJson, actual.ExtensionsJson);
    }
}
