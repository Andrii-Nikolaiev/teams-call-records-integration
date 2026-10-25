using Microsoft.Extensions.Options;
using TeamsCallRecords.Host.Web;
using TeamsCallRecords.Infrastructure.Subscriptions;

namespace TeamsCallRecords.Host.Tests;

public class PendingSubscriptionRegistryTests
{
    private const string Tenant = "11111111-1111-4111-8111-111111111111";
    private const string Resource = "communications/callRecords";
    private static readonly DateTimeOffset T0 = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now;
        public TestClock(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private static (PendingSubscriptionRegistry Registry, TestClock Clock) Create(int graceSeconds = 120)
    {
        var clock = new TestClock(T0);
        var options = Options.Create(new WebhookOptions { ClientState = "demo-only-client-state", SubscriptionCreationGraceSeconds = graceSeconds });
        return (new PendingSubscriptionRegistry(options, clock), clock);
    }

    private static string Hash(string state) => SubscriptionStore.HashClientState(state);

    [Fact]
    public void Mark_ThenTryGet_ReturnsMarker()
    {
        var (registry, _) = Create();

        registry.Mark("sub-1", Tenant, Resource, Hash("state-1"));

        Assert.True(registry.TryGet("sub-1", out var marker));
        Assert.Equal("sub-1", marker!.SubscriptionId);
        Assert.Equal(Tenant, marker.TenantId);
        Assert.Equal(Resource, marker.Resource);
        Assert.Equal(Hash("state-1"), marker.ClientStateHash);
        Assert.Equal(T0.AddSeconds(120), marker.ExpiresUtc);
    }

    [Fact]
    public void TryGet_UnknownOrEmptyId_False()
    {
        var (registry, _) = Create();
        registry.Mark("sub-1", Tenant, Resource, Hash("state-1"));

        Assert.False(registry.TryGet("other", out _));
        Assert.False(registry.TryGet("", out _));
    }

    [Fact]
    public void Marker_ExpiresAfterGraceSeconds_AndIsRemovedOnRead()
    {
        var (registry, clock) = Create();
        registry.Mark("sub-1", Tenant, Resource, Hash("state-1"));

        clock.Advance(TimeSpan.FromSeconds(119));
        Assert.True(registry.TryGet("sub-1", out _));

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(registry.TryGet("sub-1", out _));
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void CustomGraceSeconds_AreUsed()
    {
        var (registry, clock) = Create(graceSeconds: 10);
        registry.Mark("sub-1", Tenant, Resource, Hash("state-1"));

        clock.Advance(TimeSpan.FromSeconds(9));
        Assert.True(registry.TryGet("sub-1", out _));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(registry.TryGet("sub-1", out _));
    }

    [Fact]
    public void Clear_RemovesMarker_AndIgnoresUnknown()
    {
        var (registry, _) = Create();
        registry.Mark("sub-1", Tenant, Resource, Hash("state-1"));

        registry.Clear("sub-1");
        registry.Clear("unknown");

        Assert.False(registry.TryGet("sub-1", out _));
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void Mark_SameIdAgain_ReplacesMarkerAndRestartsTtl()
    {
        var (registry, clock) = Create();
        registry.Mark("sub-1", Tenant, Resource, Hash("state-1"));
        clock.Advance(TimeSpan.FromSeconds(100));

        registry.Mark("sub-1", Tenant, Resource, Hash("state-2"));
        clock.Advance(TimeSpan.FromSeconds(100));

        Assert.True(registry.TryGet("sub-1", out var marker));
        Assert.Equal(Hash("state-2"), marker!.ClientStateHash);
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void Mark_PrunesExpiredMarkers()
    {
        var (registry, clock) = Create();
        registry.Mark("old", Tenant, Resource, Hash("state-1"));
        clock.Advance(TimeSpan.FromSeconds(121));

        registry.Mark("new", Tenant, Resource, Hash("state-1"));

        Assert.Equal(1, registry.Count);
        Assert.True(registry.TryGet("new", out _));
    }

    [Fact]
    public void Mark_EmptyId_Throws()
    {
        var (registry, _) = Create();

        Assert.Throws<ArgumentException>(() => registry.Mark("", Tenant, Resource, Hash("state-1")));
    }

    [Fact]
    public void Matches_CorrectValues_True()
    {
        var (registry, _) = Create();
        registry.Mark("sub-1", Tenant, Resource, Hash("state-1"));
        registry.TryGet("sub-1", out var marker);

        Assert.True(marker!.Matches(Tenant, Resource, "state-1"));
        Assert.True(marker.Matches(Tenant.ToUpperInvariant(), "/Communications/CallRecords/", "state-1"));
    }

    [Fact]
    public void Matches_WrongTenantResourceOrState_False()
    {
        var (registry, _) = Create();
        registry.Mark("sub-1", Tenant, Resource, Hash("state-1"));
        registry.TryGet("sub-1", out var marker);

        Assert.False(marker!.Matches("22222222-2222-4222-8222-222222222222", Resource, "state-1"));
        Assert.False(marker.Matches(Tenant, "communications/onlineMeetings", "state-1"));
        Assert.False(marker.Matches(Tenant, Resource, "state-2"));
        Assert.False(marker.Matches(Tenant, Resource, ""));
    }

    [Fact]
    public void Matches_NotificationResourceUnderTheSubscribedOne_True()
    {
        var (registry, _) = Create();
        registry.Mark("sub-1", Tenant, Resource, Hash("state-1"));
        registry.TryGet("sub-1", out var marker);

        Assert.True(marker!.Matches(Tenant, "communications/callRecords/call-42", "state-1"));
        Assert.False(marker.Matches(Tenant, "communications/callRecordsOther/call-42", "state-1"));
    }

    [Theory]
    [InlineData("communications/callRecords", "communications/callRecords", true)]
    [InlineData("communications/callRecords", "/Communications/CallRecords/", true)]
    [InlineData("communications/callRecords", "communications/callRecords/abc", true)]
    [InlineData("communications/callRecords", "communications/callRecordsX", false)]
    [InlineData("communications/callRecords", "communications", false)]
    [InlineData("communications/callRecords", "", false)]
    [InlineData("", "communications/callRecords", false)]
    public void ResourceMatch_Covers(string subscribed, string notified, bool expected)
    {
        Assert.Equal(expected, ResourceMatch.Covers(subscribed, notified));
    }

    [Fact]
    public void Registry_IsUsableAsMarkerInterface()
    {
        var (registry, _) = Create();
        IPendingSubscriptionMarker marker = registry;

        marker.Mark("sub-1", Tenant, Resource, Hash("state-1"));

        Assert.True(registry.TryGet("sub-1", out _));
        marker.Clear("sub-1");
        Assert.False(registry.TryGet("sub-1", out _));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(120, true)]
    [InlineData(300, true)]
    [InlineData(0, false)]
    [InlineData(301, false)]
    public void WebhookOptions_GraceSeconds_Bounds(int seconds, bool valid)
    {
        var options = new WebhookOptions { ClientState = "demo-only-client-state", SubscriptionCreationGraceSeconds = seconds };

        Assert.Equal(valid, !options.Validate().Any());
        if (!valid) Assert.Contains("Webhook:SubscriptionCreationGraceSeconds", Assert.Single(options.Validate()));
    }

    [Fact]
    public void WebhookOptions_DefaultGraceSeconds_Is120()
    {
        Assert.Equal(120, new WebhookOptions().SubscriptionCreationGraceSeconds);
    }
}
