using static TeamsCallRecords.Core.Tests.Samples;

namespace TeamsCallRecords.Core.Tests;

public class ConverterTests
{
    private static ConversionResult Run(string json) => CallRecordConverter.Convert(json, Tenant);

    // K8: number of CDRs equals number of sessions; fields are filled.
    [Fact]
    public void OneToOne_SingleSession_ProducesOneCdr()
    {
        var r = Run(Record(Session("s1")));
        Assert.True(r.IsComplete);
        Assert.Single(r.Cdrs);
        var c = r.Cdrs[0];
        Assert.Equal("call-1", c.CallId);
        Assert.Equal("s1", c.SessionId);
        Assert.Equal(Tenant, c.TenantId);
        Assert.Equal(3L, c.SourceVersion);
        Assert.Equal(CallType.OneToOne, c.CallType);
        Assert.Equal("user", c.Caller!.Type);
        Assert.Equal("user-a", c.Caller.Id);
        Assert.Equal("user-b", c.Callee!.Id);
        Assert.Equal(60L, c.DurationSeconds);
        Assert.Equal(CdrStatus.Completed, c.Status);
        Assert.Equal(AggregationStatus.Complete, c.AggregationStatus);
        Assert.Equal(1, c.SegmentCount);
        Assert.Equal(64, c.SourceHash.Length);
    }

    [Fact]
    public void OneToOne_CanHaveSeveralSessions_CountFollowsData()
    {
        var r = Run(Record(Session("s1") + "," + Session("s2")));
        Assert.Equal(2, r.Cdrs.Count);
    }

    [Fact]
    public void GroupCall_OneCdrPerSession()
    {
        var r = Run(Record(Session("s1") + "," + Session("s2") + "," + Session("s3"), type: "groupCall"));
        Assert.Equal(3, r.Cdrs.Count);
        Assert.Equal(CallType.Group, r.Cdrs[0].CallType);
    }

    // Segments are not separate CDRs; overlapping segments are legal and not summed.
    [Fact]
    public void OverlappingSegments_DoNotChangeDuration_AndAreCounted()
    {
        var segs = Segment("a", "2026-01-01T10:00:00Z", "2026-01-01T10:00:40Z") + "," +
                   Segment("b", "2026-01-01T10:00:20Z", "2026-01-01T10:01:00Z");
        var r = Run(Record(Session("s1", segments: segs)));
        Assert.Single(r.Cdrs);
        Assert.Equal(2, r.Cdrs[0].SegmentCount);
        Assert.Equal(60L, r.Cdrs[0].DurationSeconds);
        Assert.Contains("\"segments\"", r.Cdrs[0].ExtensionsJson!);
    }

    [Fact]
    public void MissingCallerAndCallee_AreNotRejected()
    {
        var r = Run(Record(Session("s1", caller: "", callee: ""), type: "groupCall"));
        Assert.True(r.IsComplete);
        Assert.Single(r.Cdrs);
        Assert.Null(r.Cdrs[0].Caller);
        Assert.Null(r.Cdrs[0].Callee);
    }

    // Status rules (decision 2 and 6).
    [Fact]
    public void FailureInfo_GivesFailedWithReason()
    {
        var r = Run(Record(Session("s1", failure: "callSetup:busy")));
        Assert.Equal(CdrStatus.Failed, r.Cdrs[0].Status);
        Assert.Equal("callSetup:busy", r.Cdrs[0].TerminationReason);
    }

    [Fact]
    public void ShortCallWithoutFailureInfo_IsNotFailed()
    {
        var r = Run(Record(Session("s1", end: "2026-01-01T10:00:02Z")));
        Assert.Equal(CdrStatus.Completed, r.Cdrs[0].Status);
        Assert.Null(r.Cdrs[0].TerminationReason);
    }

    // Incomplete data: nothing is invented, the result is not fit for replacement.
    [Fact]
    public void NoSessions_IsIncomplete_AndCreatesNoCdr()
    {
        var r = Run("""{"id":"call-1","version":2,"type":"peerToPeer"}""");
        Assert.False(r.IsComplete);
        Assert.Empty(r.Cdrs);
        Assert.Contains("NO_SESSIONS", r.IncompleteReasons);
    }

    [Fact]
    public void EmptySessionsArray_IsIncomplete()
    {
        var r = Run(Record(""));
        Assert.False(r.IsComplete);
        Assert.Empty(r.Cdrs);
    }

    [Fact]
    public void MissingEndDate_IsIncomplete_NoZeroDurationInvented()
    {
        var r = Run(Record(Session("s1", end: null!)));
        Assert.False(r.IsComplete);
        Assert.Empty(r.Cdrs);
        Assert.Empty(r.Rejections);
        Assert.Contains("SESSION_DATES_MISSING", r.IncompleteReasons);
    }

    [Fact]
    public void IncompleteSet_MarksProducedCdrsIncomplete()
    {
        var r = Run(Record(Session("s1") + "," + Session("s2", end: null!)));
        Assert.False(r.IsComplete);
        Assert.Single(r.Cdrs);
        Assert.Equal(AggregationStatus.Incomplete, r.Cdrs[0].AggregationStatus);
    }

    // K14: invalid data goes to rejections with a code; the converter does not throw.
    [Fact]
    public void EndBeforeStart_IsRejected()
    {
        var r = Run(Record(Session("s1", start: "2026-01-01T10:01:00Z", end: "2026-01-01T10:00:00Z")));
        Assert.Single(r.Rejections);
        Assert.Equal(RejectionCodes.SessionEndBeforeStart, r.Rejections[0].Code);
        Assert.False(r.IsComplete);
    }

    [Fact]
    public void UnparsableDate_IsRejected()
    {
        var r = Run(Record(Session("s1", start: "not-a-date")));
        Assert.Equal(RejectionCodes.SessionBadDate, r.Rejections[0].Code);
    }

    [Fact]
    public void SessionWithoutId_IsRejected()
    {
        var r = Run(Record("""{"startDateTime":"2026-01-01T10:00:00Z","endDateTime":"2026-01-01T10:01:00Z"}"""));
        Assert.Equal(RejectionCodes.SessionNoId, r.Rejections[0].Code);
    }

    [Fact]
    public void BrokenJson_ReturnsRejection_NoException()
    {
        var r = Run("{ this is not json");
        Assert.Equal(RejectionCodes.BadJson, r.Rejections[0].Code);
        Assert.Empty(r.Cdrs);
    }

    [Fact]
    public void MissingVersion_IsRejected()
    {
        var r = Run("""{"id":"call-1","sessions":[]}""");
        Assert.Equal(RejectionCodes.NoVersion, r.Rejections[0].Code);
    }

    [Fact]
    public void ValidSessionSurvives_WhenAnotherIsInvalid()
    {
        var r = Run(Record(Session("s1") + "," + Session("s2", start: "bad")));
        Assert.Single(r.Cdrs);
        Assert.Single(r.Rejections);
        Assert.False(r.IsComplete);
    }

    [Fact]
    public void Time_ZoneOffsets_AreNormalizedToUtc()
    {
        var r = Run(Record(Session("s1", start: "2026-01-01T12:00:00+02:00", end: "2026-01-01T12:01:00+02:00")));
        Assert.Equal(TimeSpan.Zero, r.Cdrs[0].StartUtc.Offset);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero), r.Cdrs[0].StartUtc);
    }
}
