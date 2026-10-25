using TeamsCallRecords.Infrastructure.Config;
using TeamsCallRecords.Infrastructure.Tests.Support;

namespace TeamsCallRecords.Infrastructure.Tests;

/// <summary>Live-mode rules for subscription management (ADR-0001 D4): on only when Graph:NotificationUrl is set.</summary>
public class GraphSubscriptionOptionsTests
{
    private static GraphOptions Live(string url, string state)
    {
        var o = GraphTestOptions.Valid();
        o.NotificationUrl = url;
        o.ClientState = state;
        return o;
    }

    [Fact]
    public void Live_WithoutNotificationUrl_PollingOnly_IsValid()
    {
        Assert.Empty(Live("", "").ValidateLive());
        Assert.Empty(Live("  ", "short").ValidateLive());
    }

    [Theory]
    [InlineData("https://tcri.example.test/graph/notify")]
    [InlineData("https://tcri.example.test/graph/notify/")]
    [InlineData("https://tcri.example.test/tcri/graph/notify")]
    [InlineData("https://abc123-5000.euw.devtunnels.ms/graph/notify")]
    public void Live_ValidNotificationUrl_IsValid(string url)
    {
        Assert.Empty(Live(url, "client-state-0001").ValidateLive());
    }

    [Theory]
    [InlineData("http://tcri.example.test/graph/notify")]
    [InlineData("not a url")]
    [InlineData("/graph/notify")]
    [InlineData("https://tcri.example.test/")]
    [InlineData("https://tcri.example.test/graph/lifecycle")]
    public void Live_BadNotificationUrl_IsRejected(string url)
    {
        var errors = Live(url, "client-state-0001").ValidateLive().ToList();

        Assert.Contains(errors, e => e.Contains("Graph:NotificationUrl"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("short")]
    public void Live_NotificationUrlWithoutUsableClientState_IsRejected(string state)
    {
        var errors = Live("https://tcri.example.test/graph/notify", state).ValidateLive().ToList();

        Assert.Contains(errors, e => e.Contains("Graph:ClientState"));
    }

    [Fact]
    public void Live_ClientStateLongerThan128_IsRejected()
    {
        Assert.Contains(Live("https://tcri.example.test/graph/notify", new string('x', 129)).ValidateLive(), e => e.Contains("Graph:ClientState"));
        Assert.Empty(Live("https://tcri.example.test/graph/notify", new string('x', 128)).ValidateLive());
    }

    [Fact]
    public void Validate_CommonRules_DoNotRequireSubscriptionSettings()
    {
        Assert.Empty(Live("", "").Validate());
    }
}
