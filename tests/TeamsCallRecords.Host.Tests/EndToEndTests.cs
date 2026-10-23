using System.Net;
using System.Text;
using System.Text.Json;

namespace TeamsCallRecords.Host.Tests;

/// <summary>The whole pipeline over HTTP: notification or reprocess → inbox → worker → CDR → outbox → in-memory Graph.</summary>
public sealed class EndToEndTests : IClassFixture<TcriFactory>
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private readonly TcriFactory _f;
    public EndToEndTests(TcriFactory f) => _f = f;

    private static StringContent Json(string s) => new(s, Encoding.UTF8, "application/json");

    private static async Task<T> PollAsync<T>(Func<Task<T?>> probe) where T : class
    {
        var until = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < until)
        {
            var v = await probe();
            if (v != null) return v;
            await Task.Delay(200);
        }
        throw new TimeoutException("The expected state was not reached in time");
    }

    private async Task<JsonElement[]> CdrsAsync(DateTimeOffset around)
    {
        var r = await _f.ApiClient().GetAsync("/api/cdr?" + Samples.Range(around));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("items").EnumerateArray().Select(e => e.Clone()).ToArray();
    }

    [Fact]
    public async Task Notification_produces_cdr_and_activity_notification()
    {
        var start = DateTimeOffset.UtcNow.AddMinutes(-10);
        var callId = "e2e-notify-" + Guid.NewGuid().ToString("N");
        _f.Graph.Put(Samples.CallRecord(callId, start));

        var post = await _f.CreateClient().PostAsync("/graph/notify", Json(Samples.Notification(callId)));
        Assert.Equal(HttpStatusCode.Accepted, post.StatusCode);

        var item = await PollAsync<object>(async () =>
            (await CdrsAsync(start)).Where(c => c.GetProperty("call_id").GetString() == callId)
                .Select(c => (object)c).FirstOrDefault());
        var cdr = (JsonElement)item;
        Assert.Equal(callId + "-s1", cdr.GetProperty("session_id").GetString());
        Assert.Equal(3, cdr.GetProperty("source_version").GetInt64());
        Assert.Equal(61, cdr.GetProperty("duration_seconds").GetInt64());
        Assert.Equal("user-a", cdr.GetProperty("caller").GetProperty("id").GetString());
        Assert.Equal("user-b", cdr.GetProperty("callee").GetProperty("id").GetString());
        Assert.Equal(TcriFactory.Tenant, cdr.GetProperty("tenant_id").GetString());

        // Activity notifications reach the (in-memory) Graph.
        await PollAsync<object>(() => Task.FromResult<object?>(_f.Graph.SentNotifications.Count > 0 ? new object() : null));

        var shown = await _f.ApiClient().GetAsync("/demo/notifications");
        Assert.Equal(HttpStatusCode.OK, shown.StatusCode);
    }

    [Fact]
    public async Task Reprocess_finds_record_without_notification()
    {
        var start = DateTimeOffset.UtcNow.AddMinutes(-20);
        var callId = "e2e-reprocess-" + Guid.NewGuid().ToString("N");
        _f.Graph.Put(Samples.CallRecord(callId, start, seconds: 30));

        var r = await _f.ApiClient().PostAsync("/api/reprocess", Json(Samples.RangeBody(start)));
        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);

        await PollAsync<object>(async () =>
            (await CdrsAsync(start)).Any(c => c.GetProperty("call_id").GetString() == callId) ? new object() : null);
    }

    [Fact]
    public async Task Lifecycle_missed_starts_reconciliation()
    {
        var start = DateTimeOffset.UtcNow.AddMinutes(-30);
        var callId = "e2e-missed-" + Guid.NewGuid().ToString("N");
        _f.Graph.Put(Samples.CallRecord(callId, start));

        var body = "{\"value\":[{\"subscriptionId\":\"sub-1\",\"lifecycleEvent\":\"missed\",\"clientState\":\"" + TcriFactory.ClientState +
                   "\",\"tenantId\":\"" + TcriFactory.Tenant + "\"}]}";
        var r = await _f.CreateClient().PostAsync("/graph/lifecycle", Json(body));
        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);

        // The lookback reconciliation (24 h) lists the record and the worker converts it.
        await PollAsync<object>(async () =>
            (await CdrsAsync(start)).Any(c => c.GetProperty("call_id").GetString() == callId) ? new object() : null);
    }
}
