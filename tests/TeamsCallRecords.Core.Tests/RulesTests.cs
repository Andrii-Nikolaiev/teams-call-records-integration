using static TeamsCallRecords.Core.Tests.Samples;

namespace TeamsCallRecords.Core.Tests;

public class RulesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

    // K13: exact, no tolerance. Half a second rounds up.
    [Theory]
    [InlineData(0L, 0L)]
    [InlineData(4_990_000L, 0L)]     // 0.499 s
    [InlineData(5_000_000L, 1L)]     // 0.5 s
    [InlineData(5_010_000L, 1L)]     // 0.501 s
    [InlineData(14_999_999L, 1L)]    // 1.4999999 s
    [InlineData(15_000_000L, 2L)]    // 1.5 s
    [InlineData(600_000_000L, 60L)]
    public void Duration_RoundsHalfUp(long ticks, long expectedSeconds)
    {
        Assert.Equal(expectedSeconds, VersionRules.RoundDurationSeconds(T0, T0.AddTicks(ticks)));
    }

    [Fact]
    public void Timestamps_KeepSourcePrecision()
    {
        var r = CallRecordConverter.Convert(
            Record(Session("s1", start: "2026-01-01T10:00:00.1234567Z", end: "2026-01-01T10:00:01.6234567Z")), Tenant);
        Assert.Equal(T0.AddTicks(1_234_567), r.Cdrs[0].StartUtc);
        Assert.Equal(2L, r.Cdrs[0].DurationSeconds);
    }

    // K11: version decisions.
    [Fact]
    public void FirstTime_Applies() =>
        Assert.Equal(VersionAction.Apply, VersionRules.Decide(null, null, 1, "h"));

    [Fact]
    public void HigherVersion_Applies() =>
        Assert.Equal(VersionAction.Apply, VersionRules.Decide(2, "h1", 3, "h2"));

    [Fact]
    public void LowerVersion_IsIgnored() =>
        Assert.Equal(VersionAction.IgnoreLower, VersionRules.Decide(3, "h1", 2, "h2"));

    [Fact]
    public void SameVersion_SameHash_IsDuplicate() =>
        Assert.Equal(VersionAction.DuplicateSame, VersionRules.Decide(3, "h1", 3, "h1"));

    [Fact]
    public void SameVersion_OtherHash_IsDiscrepancy() =>
        Assert.Equal(VersionAction.Discrepancy, VersionRules.Decide(3, "h1", 3, "h2"));

    [Fact]
    public void ReprocessingSameRecord_GivesIdenticalResult()
    {
        var json = Record(Session("s1") + "," + Session("s2"));
        var a = CallRecordConverter.Convert(json, Tenant);
        var b = CallRecordConverter.Convert(json, Tenant);
        Assert.Equal(a.SessionSetHash, b.SessionSetHash);
        Assert.Equal(a.Cdrs.Select(c => c.SourceHash).ToArray()[0], b.Cdrs.Select(c => c.SourceHash).ToArray()[0]);
        Assert.Equal(VersionAction.DuplicateSame,
            VersionRules.Decide(a.SourceVersion, a.SessionSetHash, b.SourceVersion!.Value, b.SessionSetHash!));
    }
}

public class HashTests
{
    private static string HashOf(string json)
    {
        using var d = System.Text.Json.JsonDocument.Parse(json);
        return Canonical.HashOf(d.RootElement);
    }

    [Fact]
    public void PropertyOrder_DoesNotChangeHash() =>
        Assert.Equal(HashOf("""{"a":1,"b":{"x":1,"y":2}}"""), HashOf("""{"b":{"y":2,"x":1},"a":1}"""));

    [Fact]
    public void Whitespace_DoesNotChangeHash() =>
        Assert.Equal(HashOf("""{"a":1,"b":[1,2]}"""), HashOf("{ \"a\" : 1,\n \"b\" : [ 1 , 2 ] }"));

    [Fact]
    public void ArrayOrder_ChangesHash() =>
        Assert.NotEqual(HashOf("""{"a":[1,2]}"""), HashOf("""{"a":[2,1]}"""));

    [Fact]
    public void TransportAnnotations_AreExcluded() =>
        Assert.Equal(HashOf("""{"a":1}"""),
            HashOf("""{"@odata.context":"https://graph/x","segments@odata.context":"y","a":1,"b@odata.nextLink":"z"}"""));

    [Fact]
    public void ODataType_IsContent_AndChangesHash() =>
        Assert.NotEqual(HashOf("""{"a":1}"""), HashOf("""{"@odata.type":"#t","a":1}"""));

    [Fact]
    public void MediaChange_ChangesSessionHash()
    {
        var a = CallRecordConverter.Convert(Record(Session("s1")), Tenant);
        var b = CallRecordConverter.Convert(
            Record(Session("s1", segments: Segment("seg-s1").Replace("callerToCallee", "calleeToCaller"))), Tenant);
        Assert.NotEqual(a.Cdrs[0].SourceHash, b.Cdrs[0].SourceHash);
    }

    [Fact]
    public void FailureInfoChange_ChangesSessionHash()
    {
        var a = CallRecordConverter.Convert(Record(Session("s1", failure: "callSetup:busy")), Tenant);
        var b = CallRecordConverter.Convert(Record(Session("s1", failure: "callSetup:noAnswer")), Tenant);
        Assert.NotEqual(a.Cdrs[0].SourceHash, b.Cdrs[0].SourceHash);
    }

    [Fact]
    public void NumberPrecision_IsKept() =>
        Assert.NotEqual(HashOf("""{"v":1.10}"""), HashOf("""{"v":1.1}"""));

    [Fact]
    public void SessionSetHash_CoversIds()
    {
        var a = CallRecordConverter.Convert(Record(Session("s1")), Tenant);
        var b = CallRecordConverter.Convert(Record(Session("s1") + "," + Session("s2")), Tenant);
        Assert.NotEqual(a.SessionSetHash, b.SessionSetHash);
    }

    [Fact]
    public void SessionSetHash_IgnoresSessionOrder()
    {
        var a = CallRecordConverter.Convert(Record(Session("s1") + "," + Session("s2")), Tenant);
        var b = CallRecordConverter.Convert(Record(Session("s2") + "," + Session("s1")), Tenant);
        Assert.Equal(a.SessionSetHash, b.SessionSetHash);
    }
}
