using TeamsCallRecords.Infrastructure.Subscriptions;

namespace TeamsCallRecords.Infrastructure.Tests;

public class SubscriptionOptionsTests
{
    [Fact]
    public void Defaults_AreValid()
    {
        var o = new SubscriptionOptions();

        Assert.Empty(o.Validate());
        Assert.Equal(4230, o.CallRecordsLifetimeMinutes);
        Assert.Equal(60, o.ChannelMessagesLifetimeMinutes);
        Assert.Equal(5, o.RenewCheckMinutes);
        Assert.Equal("", o.TeamId);
        Assert.Equal("", o.ChannelId);
    }

    [Theory]
    [InlineData(10, true)]
    [InlineData(43200, true)]
    [InlineData(9, false)]
    [InlineData(43201, false)]
    public void CallRecordsLifetime_Bounds(int minutes, bool valid)
    {
        var o = new SubscriptionOptions { CallRecordsLifetimeMinutes = minutes };

        Assert.Equal(valid, !o.Validate().Any());
        if (!valid) Assert.Contains("Subscriptions:CallRecordsLifetimeMinutes", Assert.Single(o.Validate()));
    }

    [Theory]
    [InlineData(10, true)]
    [InlineData(43200, true)]
    [InlineData(9, false)]
    [InlineData(43201, false)]
    public void ChannelMessagesLifetime_Bounds(int minutes, bool valid)
    {
        var o = new SubscriptionOptions { ChannelMessagesLifetimeMinutes = minutes };

        Assert.Equal(valid, !o.Validate().Any());
        if (!valid) Assert.Contains("Subscriptions:ChannelMessagesLifetimeMinutes", Assert.Single(o.Validate()));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(60, true)]
    [InlineData(0, false)]
    [InlineData(61, false)]
    public void RenewCheckMinutes_Bounds(int minutes, bool valid)
    {
        var o = new SubscriptionOptions { RenewCheckMinutes = minutes };

        Assert.Equal(valid, !o.Validate().Any());
        if (!valid) Assert.Contains("Subscriptions:RenewCheckMinutes", Assert.Single(o.Validate()));
    }

    [Theory]
    [InlineData("team-1", "channel-1", true)]
    [InlineData("", "", true)]
    [InlineData("team-1", "", false)]
    [InlineData("", "channel-1", false)]
    [InlineData("  ", "channel-1", false)]
    public void TeamAndChannel_BothOrNone(string team, string channel, bool valid)
    {
        var o = new SubscriptionOptions { TeamId = team, ChannelId = channel };

        Assert.Equal(valid, !o.Validate().Any());
        if (!valid) Assert.Contains("TeamId", Assert.Single(o.Validate()));
    }
}
