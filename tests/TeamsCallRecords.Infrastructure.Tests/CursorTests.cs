using TeamsCallRecords.Infrastructure.Data;

namespace TeamsCallRecords.Infrastructure.Tests;

public class CursorTests
{
    private const string Start = "2026-10-20T09:00:00.0000000Z";

    [Fact]
    public void Encode_Example_RoundTrips()
    {
        var c = Cursor.Encode(Start, 17);
        Assert.Equal((Start, 17L), Cursor.Decode(c));
    }

    [Theory]
    [InlineData(1L)]
    [InlineData(255L)]
    [InlineData(long.MaxValue)]
    public void RoundTrip_ManyValues(long seq)
    {
        var c = Cursor.Encode(Start, seq);
        Assert.DoesNotContain('+', c);
        Assert.DoesNotContain('/', c);
        Assert.DoesNotContain('=', c);
        Assert.Equal((Start, seq), Cursor.Decode(c));
    }

    [Theory]
    [InlineData("2026-10-20T09:00:00Z", 1L)]
    [InlineData("", 1L)]
    [InlineData(Start, 0L)]
    [InlineData(Start, -5L)]
    public void Encode_Invalid_Throws(string start, long seq) =>
        Assert.Throws<ArgumentException>(() => Cursor.Encode(start, seq));

    public static IEnumerable<object[]> Malformed()
    {
        static string B64(string s) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(s)).TrimEnd('=');
        yield return new object[] { "" };
        yield return new object[] { "!!!!" };
        yield return new object[] { B64("no-delimiter") };
        yield return new object[] { B64(Start + "|1|2") };
        yield return new object[] { B64("2026-10-20T09:00:00Z|1") };
        yield return new object[] { B64(Start + "|0") };
        yield return new object[] { B64(Start + "|-1") };
        yield return new object[] { B64(Start + "|12x") };
        yield return new object[] { B64(Start + "|") };
        yield return new object[] { B64(Start + "|99999999999999999999") };
    }

    [Theory]
    [MemberData(nameof(Malformed))]
    public void Decode_Malformed_ThrowsArgumentException(string cursor)
    {
        var ex = Assert.Throws<ArgumentException>(() => Cursor.Decode(cursor));
        Assert.Equal("cursor", ex.ParamName);
    }
}
