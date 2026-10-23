using Microsoft.Extensions.Time.Testing;
using TeamsCallRecords.Infrastructure.Data;
using TeamsCallRecords.Infrastructure.Tests.Support;

namespace TeamsCallRecords.Infrastructure.Tests;

public class UtcTextTests
{
    [Fact]
    public void Format_SevenDigitsAndZ()
    {
        var value = DateTimeOffset.Parse("2026-10-20T12:00:00Z");
        Assert.Equal("2026-10-20T12:00:00.0000000Z", UtcText.Format(value));
    }

    [Fact]
    public void Format_ConvertsOffsetToUtc()
    {
        var value = DateTimeOffset.Parse("2026-10-20T12:00:00+02:00");
        Assert.Equal("2026-10-20T10:00:00.0000000Z", UtcText.Format(value));
    }

    [Fact]
    public void Parse_RoundTripKeepsTicks()
    {
        var original = new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero).AddTicks(1234567);
        var parsed = UtcText.Parse(UtcText.Format(original));
        Assert.Equal(original.UtcTicks, parsed.UtcTicks);
        Assert.Equal(TimeSpan.Zero, parsed.Offset);
    }

    [Fact]
    public void Format_OrderEqualsLexicographicOrder()
    {
        var times = new[]
        {
            new DateTimeOffset(2026, 10, 20, 18, 0, 0, TimeSpan.Zero).AddTicks(5),
            new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
            new DateTimeOffset(2026, 10, 20, 18, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 10, 20, 18, 0, 1, TimeSpan.Zero),
            new DateTimeOffset(2025, 12, 31, 23, 59, 59, TimeSpan.Zero).AddTicks(9999999),
        };
        var byTime = times.OrderBy(t => t.UtcTicks).Select(UtcText.Format).ToList();
        var byText = times.Select(UtcText.Format).OrderBy(s => s, StringComparer.Ordinal).ToList();
        Assert.Equal(byTime, byText);
    }

    [Fact]
    public void Now_UsesInjectedClock()
    {
        var clock = new FakeTimeProvider(TestDb.T0);
        Assert.Equal("2026-10-20T18:00:00.0000000Z", UtcText.Now(clock));
        clock.Advance(TimeSpan.FromSeconds(90));
        Assert.Equal("2026-10-20T18:01:30.0000000Z", UtcText.Now(clock));
    }
}
