using System.Net;
using System.Text;
using TeamsCallRecords.Host.Web;
using TeamsCallRecords.Infrastructure.Data.Entities;
using TeamsCallRecords.Infrastructure.Subscriptions;

namespace TeamsCallRecords.Host.Tests;

/// <summary>Webhook with managed subscriptions (ADR-0001 D2, D3, DV-2): per-subscription clientState, lifecycle routing, creation race.</summary>
public sealed class ManagedWebhookTests : IClassFixture<ManagedWebhookFactory>
{
    private const string Res = ManagedWebhookFactory.Resource;
    private const string State = ManagedWebhookFactory.State;
    private const string Tenant = ManagedWebhookFactory.Tenant;
    private const string OtherTenant = "22222222-2222-4222-8222-222222222222";

    private readonly ManagedWebhookFactory _f;
    public ManagedWebhookTests(ManagedWebhookFactory f) => _f = f;

    private static StringContent Json(string s) => new(s, Encoding.UTF8, "application/json");

    private static string Element(string subscription, string state = State, string? tenant = Tenant, string? resource = null,
        string? lifecycleEvent = null)
    {
        var sb = new StringBuilder("{\"subscriptionId\":\"" + subscription + "\",\"clientState\":\"" + state + "\"");
        if (tenant != null) sb.Append(",\"tenantId\":\"" + tenant + "\"");
        if (resource != null) sb.Append(",\"resource\":\"" + resource + "\"");
        sb.Append(lifecycleEvent != null ? ",\"lifecycleEvent\":\"" + lifecycleEvent + "\"" : ",\"changeType\":\"created\"");
        return sb.Append('}').ToString();
    }

    private static string Batch(params string[] elements) => "{\"value\":[" + string.Join(",", elements) + "]}";

    private Task<HttpResponseMessage> Notify(params string[] elements) => _f.CreateClient().PostAsync("/graph/notify", Json(Batch(elements)));
    private Task<HttpResponseMessage> Lifecycle(params string[] elements) => _f.CreateClient().PostAsync("/graph/lifecycle", Json(Batch(elements)));

    private IEnumerable<string?> QueuedCallIds() => _f.Inbox.Items.Select(i => i.CallId);

    private static async Task<bool> WaitFor(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < until)
        {
            if (condition()) return true;
            await Task.Delay(50);
        }
        return condition();
    }

    // ---- change notifications -----------------------------------------------------------------------------

    [Fact]
    public async Task Notify_KnownSubscription_CorrectState_IsAcceptedAndQueued()
    {
        await _f.Seed("sub-ok");

        var r = await Notify(Element("sub-ok", resource: Res + "/call-ok"));

        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        var item = Assert.Single(_f.Inbox.Items, i => i.CallId == "call-ok");
        Assert.Equal(Tenant, item.TenantId);
        Assert.DoesNotContain(State, item.Raw);
    }

    [Fact]
    public async Task Notify_WrongClientState_IsForbidden()
    {
        await _f.Seed("sub-wrong-state");

        var r = await Notify(Element("sub-wrong-state", state: "some-other-state-01", resource: Res + "/call-ws"));

        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.DoesNotContain("call-ws", QueuedCallIds());
    }

    [Fact]
    public async Task Notify_FixedDemoClientState_IsNotAcceptedForSubscriptions()
    {
        await _f.Seed("sub-demo-state");

        var r = await Notify(Element("sub-demo-state", state: TcriFactory.ClientState, resource: Res + "/call-demo"));

        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    public async Task Notify_UnknownSubscription_IsForbidden()
    {
        var r = await Notify(Element("sub-ghost", resource: Res + "/call-ghost"));

        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.DoesNotContain("call-ghost", QueuedCallIds());
    }

    [Fact]
    public async Task Notify_FailedSubscription_IsForbidden()
    {
        await _f.Seed("sub-failed", status: SubscriptionStatuses.Failed);

        var r = await Notify(Element("sub-failed", resource: Res + "/call-failed"));

        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    public async Task Notify_WrongOrMissingTenant_IsForbidden()
    {
        await _f.Seed("sub-tenant");

        Assert.Equal(HttpStatusCode.Forbidden, (await Notify(Element("sub-tenant", tenant: OtherTenant, resource: Res + "/c"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Notify(Element("sub-tenant", tenant: null, resource: Res + "/c"))).StatusCode);
    }

    [Fact]
    public async Task Notify_ResourceOutsideTheSubscription_IsForbidden()
    {
        await _f.Seed("sub-resource");

        var other = await Notify(Element("sub-resource", resource: "communications/onlineMeetings/x"));
        var missing = await Notify(Element("sub-resource", resource: null));

        Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, missing.StatusCode);
    }

    [Fact]
    public async Task Notify_MixedBatch_KeepsValidAndDropsInvalid()
    {
        await _f.Seed("sub-mixed");

        var r = await Notify(Element("sub-mixed", resource: Res + "/call-mixed-1"),
            Element("sub-mixed", state: "some-other-state-01", resource: Res + "/call-mixed-2"));

        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        Assert.Contains("call-mixed-1", QueuedCallIds());
        Assert.DoesNotContain("call-mixed-2", QueuedCallIds());
    }

    [Fact]
    public async Task Notify_InboxFailure_Gives503()
    {
        await _f.Seed("sub-inbox-down");
        _f.Inbox.Fail = true;
        try
        {
            var r = await Notify(Element("sub-inbox-down", resource: Res + "/call-down"));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
        }
        finally { _f.Inbox.Fail = false; }
    }

    [Fact]
    public async Task Notify_ValidationHandshake_StillEchoesToken()
    {
        var r = await _f.CreateClient().PostAsync("/graph/notify?validationToken=tok%20en", null);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("tok en", await r.Content.ReadAsStringAsync());
    }

    // ---- creation race (DV-2) -----------------------------------------------------------------------------

    [Fact]
    public async Task Notify_SubscriptionBeingCreated_Gives503AndKeepsValidItems()
    {
        await _f.Seed("sub-known");
        _f.Pending.Mark("sub-creating", Tenant, Res, SubscriptionStore.HashClientState(State));

        var r = await Notify(Element("sub-known", resource: Res + "/call-known"), Element("sub-creating", resource: Res + "/call-creating"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(5), r.Headers.RetryAfter?.Delta);
        Assert.Contains("call-known", QueuedCallIds());
        Assert.DoesNotContain("call-creating", QueuedCallIds());
    }

    [Fact]
    public async Task Notify_PendingMarkerWithWrongState_IsForbidden()
    {
        _f.Pending.Mark("sub-creating-bad", Tenant, Res, SubscriptionStore.HashClientState(State));

        var r = await Notify(Element("sub-creating-bad", state: "some-other-state-01", resource: Res + "/c"));

        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    public async Task Notify_AfterRowIsSavedAndMarkerCleared_IsAccepted()
    {
        _f.Pending.Mark("sub-saved", Tenant, Res, SubscriptionStore.HashClientState(State));
        await _f.Seed("sub-saved");
        _f.Pending.Clear("sub-saved");

        var r = await Notify(Element("sub-saved", resource: Res + "/call-saved"));

        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
    }

    [Fact]
    public async Task Notify_ExpiredPendingMarker_IsForbidden()
    {
        _f.Pending.Mark("sub-expired", Tenant, Res, SubscriptionStore.HashClientState(State));
        await Task.Delay(TimeSpan.FromSeconds(2.5));

        var r = await Notify(Element("sub-expired", resource: Res + "/c"));

        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    // ---- lifecycle ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("/graph/lifecycle")]
    [InlineData("/graph/notify")]
    public async Task Lifecycle_Reauthorization_IsHandedToTheService(string path)
    {
        var id = "sub-reauth-" + (path == "/graph/lifecycle" ? "a" : "b");
        await _f.Seed(id);

        var r = await _f.CreateClient().PostAsync(path, Json(Batch(Element(id, lifecycleEvent: "reauthorizationRequired"))));

        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        Assert.True(await WaitFor(() => _f.Subscriptions.Calls.Contains(id + ":reauthorizationRequired")));
    }

    [Fact]
    public async Task Lifecycle_SubscriptionRemoved_IsHandedToTheService()
    {
        await _f.Seed("sub-removed");

        var r = await Lifecycle(Element("sub-removed", lifecycleEvent: "subscriptionRemoved"));

        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        Assert.True(await WaitFor(() => _f.Subscriptions.Calls.Contains("sub-removed:subscriptionRemoved")));
    }

    [Fact]
    public async Task Lifecycle_Missed_IsHandledBeforeTheAnswer()
    {
        await _f.Seed("sub-missed");

        var r = await Lifecycle(Element("sub-missed", lifecycleEvent: "missed"));

        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        Assert.Contains("sub-missed:missed", _f.Subscriptions.Calls);
    }

    [Fact]
    public async Task Lifecycle_MissedFails_Gives503()
    {
        await _f.Seed("sub-missed-down");
        _f.Subscriptions.Fail = true;
        try
        {
            var r = await Lifecycle(Element("sub-missed-down", lifecycleEvent: "missed"));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
        }
        finally { _f.Subscriptions.Fail = false; }
    }

    [Fact]
    public async Task Lifecycle_WrongStateOrUnknownSubscription_IsForbidden_AndNothingIsCalled()
    {
        await _f.Seed("sub-lc-guard");

        var wrongState = await Lifecycle(Element("sub-lc-guard", state: "some-other-state-01", lifecycleEvent: "subscriptionRemoved"));
        var unknown = await Lifecycle(Element("sub-lc-ghost", lifecycleEvent: "subscriptionRemoved"));
        var wrongResource = await Lifecycle(Element("sub-lc-guard", resource: "communications/onlineMeetings", lifecycleEvent: "subscriptionRemoved"));

        Assert.Equal(HttpStatusCode.Forbidden, wrongState.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, wrongResource.StatusCode);
        await Task.Delay(200);
        Assert.DoesNotContain(_f.Subscriptions.Calls, c => c.StartsWith("sub-lc-"));
    }

    [Fact]
    public async Task Lifecycle_SubscriptionBeingCreated_Gives503()
    {
        _f.Pending.Mark("sub-lc-creating", Tenant, Res, SubscriptionStore.HashClientState(State));

        var r = await Lifecycle(Element("sub-lc-creating", lifecycleEvent: "reauthorizationRequired"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
    }
}
