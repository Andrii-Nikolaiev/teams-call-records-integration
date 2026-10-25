using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using TeamsCallRecords.Infrastructure.Config;
using TeamsCallRecords.Infrastructure.Graph;
using TeamsCallRecords.Infrastructure.Tests.Support;

namespace TeamsCallRecords.Infrastructure.Tests;

/// <summary>GraphClient against an HTTP stub (no network, no resilience handler; that is tested separately).</summary>
public class GraphClientTests
{
    private const string Base = "https://graph.microsoft.com/v1.0/";

    private static (GraphClient Client, StubHandler Stub, FakeCredential Credential) Build(
        Func<SeenRequest, int, HttpResponseMessage> responder, Action<GraphOptions>? tune = null)
    {
        var options = GraphTestOptions.Valid();
        tune?.Invoke(options);
        var stub = new StubHandler(responder);
        var credential = new FakeCredential();
        var client = new GraphClient(new HttpClient(stub), credential, GraphTestOptions.Wrap(options), NullLogger<GraphClient>.Instance);
        return (client, stub, credential);
    }

    private static string Segments(params string[] ids) => string.Join(",", ids.Select(i => $"{{\"id\":\"{i}\"}}"));

    // ---- GetCallRecordAsync ---------------------------------------------------------------------------------

    [Fact]
    public async Task GetCallRecord_SinglePage_SendsExpandAndBearerToken()
    {
        var (client, stub, _) = Build((_, _) => StubHandler.Json("{\"id\":\"c1\",\"sessions\":[{\"id\":\"s1\",\"segments\":[{\"id\":\"g1\"}]}]}"));

        var result = await client.GetCallRecordAsync("c1", CancellationToken.None);

        Assert.Equal(GraphFetchStatus.Ok, result.Status);
        Assert.Equal("c1", JsonNode.Parse(result.Json!)!["id"]!.GetValue<string>());
        var seen = Assert.Single(stub.Requests);
        Assert.Equal(HttpMethod.Get, seen.Method);
        Assert.Equal("/v1.0/communications/callRecords/c1", seen.Uri.AbsolutePath);
        Assert.Equal("?$expand=sessions($expand=segments)", Uri.UnescapeDataString(seen.Uri.Query));
        Assert.Equal("Bearer test-token", seen.Authorization);
    }

    [Fact]
    public async Task GetCallRecord_EscapesCallId()
    {
        var (client, stub, _) = Build((_, _) => StubHandler.Json("{\"id\":\"a/b\"}"));

        await client.GetCallRecordAsync("a/b c", CancellationToken.None);

        Assert.Contains("a%2Fb%20c", stub.Requests[0].Uri.AbsoluteUri);
    }

    [Fact]
    public async Task GetCallRecord_404_IsNotFoundNotException()
    {
        var (client, _, _) = Build((_, _) => StubHandler.Status(HttpStatusCode.NotFound));

        var result = await client.GetCallRecordAsync("c1", CancellationToken.None);

        Assert.Equal(GraphFetchStatus.NotFound, result.Status);
        Assert.Null(result.Json);
    }

    [Fact]
    public async Task GetCallRecord_FirstPageNotJson_IsMalformedWithText()
    {
        var (client, _, _) = Build((_, _) => StubHandler.Json("<html>oops</html>"));

        var result = await client.GetCallRecordAsync("c1", CancellationToken.None);

        Assert.Equal(GraphFetchStatus.Malformed, result.Status);
        Assert.Equal("<html>oops</html>", result.Json);
    }

    [Fact]
    public async Task GetCallRecord_SessionsPaging_AppendsPagesAndRemovesLink()
    {
        var next = Base + "communications/callRecords/c1/sessions?$skiptoken=p2";
        var (client, stub, _) = Build((r, n) => n == 1
            ? StubHandler.Json("{\"id\":\"c1\",\"sessions\":[{\"id\":\"s1\"}],\"sessions@odata.nextLink\":\"" + next + "\"}")
            : StubHandler.Json("{\"value\":[{\"id\":\"s2\"},{\"id\":\"s3\"}]}"));

        var result = await client.GetCallRecordAsync("c1", CancellationToken.None);

        Assert.Equal(GraphFetchStatus.Ok, result.Status);
        var root = JsonNode.Parse(result.Json!)!.AsObject();
        Assert.Equal(new[] { "s1", "s2", "s3" }, root["sessions"]!.AsArray().Select(s => s!["id"]!.GetValue<string>()));
        Assert.False(root.ContainsKey("sessions@odata.nextLink"));
        Assert.Equal(2, stub.Requests.Count);
        Assert.Equal(next, stub.Requests[1].Uri.AbsoluteUri);
        Assert.Equal("Bearer test-token", stub.Requests[1].Authorization);
    }

    [Fact]
    public async Task GetCallRecord_SegmentsPaging_AppendsToTheSession()
    {
        var next = Base + "communications/callRecords/c1/sessions/s1/segments?$skiptoken=p2";
        var (client, _, _) = Build((r, n) => n == 1
            ? StubHandler.Json("{\"id\":\"c1\",\"sessions\":[{\"id\":\"s1\",\"segments\":[{\"id\":\"g1\"}],\"segments@odata.nextLink\":\"" + next + "\"},{\"id\":\"s2\",\"segments\":[]}]}")
            : StubHandler.Json("{\"value\":[{\"id\":\"g2\"}]}"));

        var result = await client.GetCallRecordAsync("c1", CancellationToken.None);

        var root = JsonNode.Parse(result.Json!)!.AsObject();
        var s1 = root["sessions"]![0]!.AsObject();
        Assert.Equal(new[] { "g1", "g2" }, s1["segments"]!.AsArray().Select(s => s!["id"]!.GetValue<string>()));
        Assert.False(s1.ContainsKey("segments@odata.nextLink"));
        Assert.Empty(root["sessions"]![1]!["segments"]!.AsArray());
    }

    [Fact]
    public async Task GetCallRecord_MorePagesThanMaxPages_IsPagesIncomplete()
    {
        var next = Base + "communications/callRecords/c1/sessions?$skiptoken=again";
        var (client, stub, _) = Build((r, n) => n == 1
            ? StubHandler.Json("{\"id\":\"c1\",\"sessions\":[{\"id\":\"s1\"}],\"sessions@odata.nextLink\":\"" + next + "\"}")
            : StubHandler.Json("{\"value\":[{\"id\":\"s" + n + "\"}],\"@odata.nextLink\":\"" + next + "\"}"),
            o => o.MaxPages = 2);

        var result = await client.GetCallRecordAsync("c1", CancellationToken.None);

        Assert.Equal(GraphFetchStatus.PagesIncomplete, result.Status);
        Assert.Equal(2, stub.Requests.Count);
    }

    [Fact]
    public async Task GetCallRecord_LaterPageNotJson_IsMalformedWithThatText()
    {
        var next = Base + "communications/callRecords/c1/sessions?$skiptoken=p2";
        var (client, _, _) = Build((r, n) => n == 1
            ? StubHandler.Json("{\"id\":\"c1\",\"sessions\":[],\"sessions@odata.nextLink\":\"" + next + "\"}")
            : StubHandler.Json("not json"));

        var result = await client.GetCallRecordAsync("c1", CancellationToken.None);

        Assert.Equal(GraphFetchStatus.Malformed, result.Status);
        Assert.Equal("not json", result.Json);
    }

    [Fact]
    public async Task GetCallRecord_LinkToOtherHost_ThrowsAndNeverSendsToken()
    {
        var (client, stub, _) = Build((r, n) => StubHandler.Json(
            "{\"id\":\"c1\",\"sessions\":[],\"sessions@odata.nextLink\":\"https://evil.example.com/steal\"}"));

        var ex = await Assert.ThrowsAsync<GraphException>(() => client.GetCallRecordAsync("c1", CancellationToken.None));

        Assert.False(ex.IsTransient);
        Assert.Single(stub.Requests);   // nothing was sent to the foreign host
    }

    // ---- error classification -------------------------------------------------------------------------------

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(409)]
    public async Task Get_Client4xx_IsNotTransient(int code)
    {
        var (client, _, _) = Build((_, _) => StubHandler.Status((HttpStatusCode)code, "secret-body"));

        var ex = await Assert.ThrowsAsync<GraphException>(() => client.GetCallRecordAsync("c1", CancellationToken.None));

        Assert.Equal(code, ex.StatusCode);
        Assert.False(ex.IsTransient);
        Assert.False(ex.OutcomeUnknown);
        Assert.DoesNotContain("secret-body", ex.Message);
    }

    [Fact]
    public async Task Get_429_IsTransientAndKnownOutcome()
    {
        var (client, _, _) = Build((_, _) => StubHandler.Status((HttpStatusCode)429));

        var ex = await Assert.ThrowsAsync<GraphException>(() => client.GetCallRecordAsync("c1", CancellationToken.None));

        Assert.Equal(429, ex.StatusCode);
        Assert.True(ex.IsTransient);
        Assert.False(ex.OutcomeUnknown);
    }

    [Fact]
    public async Task Get_503_IsTransientAndOutcomeKnownForReads()
    {
        var (client, _, _) = Build((_, _) => StubHandler.Status(HttpStatusCode.ServiceUnavailable));

        var ex = await Assert.ThrowsAsync<GraphException>(() => client.GetCallRecordAsync("c1", CancellationToken.None));

        Assert.True(ex.IsTransient);
        Assert.False(ex.OutcomeUnknown);
    }

    [Fact]
    public async Task Post_503_OutcomeIsUnknown()
    {
        var (client, _, _) = Build((_, _) => StubHandler.Status(HttpStatusCode.ServiceUnavailable));

        var ex = await Assert.ThrowsAsync<GraphException>(() => client.CreateSubscriptionAsync(SubscriptionRequest(), CancellationToken.None));

        Assert.Equal(503, ex.StatusCode);
        Assert.True(ex.IsTransient);
        Assert.True(ex.OutcomeUnknown);
    }

    [Fact]
    public async Task Post_429_OutcomeIsKnown()
    {
        var (client, _, _) = Build((_, _) => StubHandler.Status((HttpStatusCode)429));

        var ex = await Assert.ThrowsAsync<GraphException>(() => client.CreateSubscriptionAsync(SubscriptionRequest(), CancellationToken.None));

        Assert.True(ex.IsTransient);
        Assert.False(ex.OutcomeUnknown);
    }

    [Fact]
    public async Task Post_400_IsNotTransient()
    {
        var (client, _, _) = Build((_, _) => StubHandler.Status(HttpStatusCode.BadRequest));

        var ex = await Assert.ThrowsAsync<GraphException>(() => client.CreateSubscriptionAsync(SubscriptionRequest(), CancellationToken.None));

        Assert.False(ex.IsTransient);
        Assert.False(ex.OutcomeUnknown);
    }

    [Fact]
    public async Task NetworkError_Get_IsTransientWithKnownOutcome()
    {
        var (client, _, _) = Build((_, _) => throw new HttpRequestException("boom"));

        var ex = await Assert.ThrowsAsync<GraphException>(() => client.GetCallRecordAsync("c1", CancellationToken.None));

        Assert.Null(ex.StatusCode);
        Assert.True(ex.IsTransient);
        Assert.False(ex.OutcomeUnknown);
    }

    [Fact]
    public async Task NetworkError_Post_OutcomeIsUnknown()
    {
        var (client, _, _) = Build((_, _) => throw new HttpRequestException("boom"));

        var ex = await Assert.ThrowsAsync<GraphException>(() => client.SendActivityNotificationAsync(Activity(), CancellationToken.None));

        Assert.True(ex.IsTransient);
        Assert.True(ex.OutcomeUnknown);
    }

    [Fact]
    public async Task CallerCancellation_PropagatesAsCancellation()
    {
        var (client, _, _) = Build((_, _) => throw new TaskCanceledException());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetCallRecordAsync("c1", cts.Token));
    }

    [Fact]
    public async Task TokenFailure_IsNonTransientAndDoesNotLeakDetails()
    {
        var (client, stub, credential) = Build((_, _) => StubHandler.Json("{}"));
        credential.Fail = true;

        var ex = await Assert.ThrowsAsync<GraphException>(() => client.GetCallRecordAsync("c1", CancellationToken.None));

        Assert.Equal("token acquisition failed", ex.Message);
        Assert.Null(ex.StatusCode);
        Assert.False(ex.IsTransient);
        Assert.Empty(stub.Requests);
    }

    // ---- ListCallRecordsAsync -------------------------------------------------------------------------------

    [Fact]
    public async Task ListCallRecords_SendsFilter_ParsesItems_SkipsItemsWithoutId()
    {
        var (client, stub, _) = Build((_, _) => StubHandler.Json(
            "{\"value\":[" +
            "{\"id\":\"a\",\"version\":3,\"startDateTime\":\"2026-10-20T10:00:00Z\"}," +
            "{\"id\":\"b\",\"version\":null,\"startDateTime\":\"2026-10-20T11:30:00.1234567Z\"}," +
            "{\"version\":1}," +
            "{\"id\":\"c\"}]}"));

        var list = await client.ListCallRecordsAsync(
            DateTimeOffset.Parse("2026-10-20T00:00:00Z"), DateTimeOffset.Parse("2026-10-21T00:00:00Z"), CancellationToken.None);

        Assert.Equal(new[] { "a", "b", "c" }, list.Select(x => x.Id));
        Assert.Equal(3L, list[0].Version);
        Assert.Null(list[1].Version);
        Assert.Equal(DateTimeOffset.Parse("2026-10-20T10:00:00Z"), list[0].StartUtc);
        Assert.Null(list[2].StartUtc);

        var seen = Assert.Single(stub.Requests);
        Assert.Equal("/v1.0/communications/callRecords", seen.Uri.AbsolutePath);
        Assert.Equal("?$filter=startDateTime ge 2026-10-20T00:00:00.0000000Z and startDateTime lt 2026-10-21T00:00:00.0000000Z",
            Uri.UnescapeDataString(seen.Uri.Query));
    }

    [Fact]
    public async Task ListCallRecords_FollowsNextLink()
    {
        var next = Base + "communications/callRecords?$skiptoken=p2";
        var (client, stub, _) = Build((r, n) => n == 1
            ? StubHandler.Json("{\"value\":[{\"id\":\"a\"}],\"@odata.nextLink\":\"" + next + "\"}")
            : StubHandler.Json("{\"value\":[{\"id\":\"b\"}]}"));

        var list = await client.ListCallRecordsAsync(DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(new[] { "a", "b" }, list.Select(x => x.Id));
        Assert.Equal(next, stub.Requests[1].Uri.AbsoluteUri);
    }

    [Fact]
    public async Task ListCallRecords_MoreThanMaxPages_Throws()
    {
        var next = Base + "communications/callRecords?$skiptoken=again";
        var (client, _, _) = Build((_, _) => StubHandler.Json("{\"value\":[{\"id\":\"x\"}],\"@odata.nextLink\":\"" + next + "\"}"),
            o => o.MaxPages = 2);

        var ex = await Assert.ThrowsAsync<GraphException>(
            () => client.ListCallRecordsAsync(DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow, CancellationToken.None));

        Assert.False(ex.IsTransient);
    }

    [Fact]
    public async Task ListCallRecords_NotJson_Throws()
    {
        var (client, _, _) = Build((_, _) => StubHandler.Json("nope"));

        var ex = await Assert.ThrowsAsync<GraphException>(
            () => client.ListCallRecordsAsync(DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow, CancellationToken.None));

        Assert.False(ex.IsTransient);
        Assert.Null(ex.StatusCode);
    }

    // ---- subscriptions --------------------------------------------------------------------------------------

    private static GraphSubscriptionRequest SubscriptionRequest() => new(
        "callRecords", "/communications/callRecords", "https://example.com/graph/notify",
        DateTimeOffset.Parse("2026-10-28T10:00:00Z"), "client-state-value");

    private static ActivityNotification Activity() => new("user-1", "callCompleted", "Call completed", "Call with user-2",
        new Dictionary<string, string> { ["duration"] = "61", ["peer"] = "user-2" });

    [Fact]
    public async Task CreateSubscription_SendsBody_ReturnsResult()
    {
        var (client, stub, _) = Build((_, _) => StubHandler.Json(
            "{\"id\":\"sub-1\",\"resource\":\"/communications/callRecords\",\"expirationDateTime\":\"2026-10-28T10:00:00Z\"}", HttpStatusCode.Created));

        var sub = await client.CreateSubscriptionAsync(SubscriptionRequest(), CancellationToken.None);

        Assert.Equal(new GraphSubscription("sub-1", "/communications/callRecords", DateTimeOffset.Parse("2026-10-28T10:00:00Z")), sub);
        var seen = Assert.Single(stub.Requests);
        Assert.Equal(HttpMethod.Post, seen.Method);
        Assert.Equal("/v1.0/subscriptions", seen.Uri.AbsolutePath);
        var body = JsonNode.Parse(seen.Body!)!.AsObject();
        Assert.Equal("created", body["changeType"]!.GetValue<string>());
        Assert.Equal("https://example.com/graph/notify", body["notificationUrl"]!.GetValue<string>());
        Assert.Equal("https://example.com/graph/notify", body["lifecycleNotificationUrl"]!.GetValue<string>());
        Assert.Equal("/communications/callRecords", body["resource"]!.GetValue<string>());
        Assert.Equal("2026-10-28T10:00:00.0000000Z", body["expirationDateTime"]!.GetValue<string>());
        Assert.Equal("client-state-value", body["clientState"]!.GetValue<string>());
    }

    [Fact]
    public async Task CreateSubscription_ResponseWithoutFields_Throws()
    {
        var (client, _, _) = Build((_, _) => StubHandler.Json("{}", HttpStatusCode.Created));

        var ex = await Assert.ThrowsAsync<GraphException>(() => client.CreateSubscriptionAsync(SubscriptionRequest(), CancellationToken.None));

        Assert.False(ex.IsTransient);
    }

    [Fact]
    public async Task RenewSubscription_SendsPatch_ResourceMayBeAbsent()
    {
        var (client, stub, _) = Build((_, _) => StubHandler.Json("{\"id\":\"sub-1\",\"expirationDateTime\":\"2026-10-29T10:00:00Z\"}"));

        var sub = await client.RenewSubscriptionAsync("sub-1", DateTimeOffset.Parse("2026-10-29T10:00:00Z"), CancellationToken.None);

        Assert.Equal(new GraphSubscription("sub-1", "", DateTimeOffset.Parse("2026-10-29T10:00:00Z")), sub);
        var seen = Assert.Single(stub.Requests);
        Assert.Equal(HttpMethod.Patch, seen.Method);
        Assert.Equal("/v1.0/subscriptions/sub-1", seen.Uri.AbsolutePath);
        Assert.Equal("{\"expirationDateTime\":\"2026-10-29T10:00:00.0000000Z\"}", seen.Body);
    }

    [Theory]
    [InlineData(204)]
    [InlineData(404)]
    public async Task DeleteSubscription_204And404AreSuccess(int code)
    {
        var (client, stub, _) = Build((_, _) => StubHandler.Status((HttpStatusCode)code));

        await client.DeleteSubscriptionAsync("sub-1", CancellationToken.None);

        var seen = Assert.Single(stub.Requests);
        Assert.Equal(HttpMethod.Delete, seen.Method);
        Assert.Equal("/v1.0/subscriptions/sub-1", seen.Uri.AbsolutePath);
    }

    [Fact]
    public async Task DeleteSubscription_500_Throws()
    {
        var (client, _, _) = Build((_, _) => StubHandler.Status(HttpStatusCode.InternalServerError));

        var ex = await Assert.ThrowsAsync<GraphException>(() => client.DeleteSubscriptionAsync("sub-1", CancellationToken.None));

        Assert.True(ex.IsTransient);
        Assert.False(ex.OutcomeUnknown);   // DELETE is safe to repeat
    }

    [Fact]
    public async Task ListSubscriptions_ParsesAndSkipsIncomplete()
    {
        var (client, stub, _) = Build((_, _) => StubHandler.Json(
            "{\"value\":[" +
            "{\"id\":\"s1\",\"resource\":\"/communications/callRecords\",\"notificationUrl\":\"https://x/n\",\"expirationDateTime\":\"2026-10-28T10:00:00Z\"}," +
            "{\"id\":\"s2\",\"resource\":\"r\",\"notificationUrl\":\"https://x/n\",\"expirationDateTime\":\"garbage\"}," +
            "{\"id\":\"s3\",\"notificationUrl\":\"https://x/n\",\"expirationDateTime\":\"2026-10-28T10:00:00Z\"}]}"));

        var list = await client.ListSubscriptionsAsync(CancellationToken.None);

        var only = Assert.Single(list);
        Assert.Equal(new GraphSubscriptionInfo("s1", "/communications/callRecords", "https://x/n", DateTimeOffset.Parse("2026-10-28T10:00:00Z")), only);
        Assert.Equal("/v1.0/subscriptions", stub.Requests[0].Uri.AbsolutePath);
    }

    // ---- activity feed --------------------------------------------------------------------------------------

    [Fact]
    public async Task SendActivityNotification_SendsBodyInOrder()
    {
        var (client, stub, _) = Build((_, _) => StubHandler.Status(HttpStatusCode.NoContent));

        await client.SendActivityNotificationAsync(Activity(), CancellationToken.None);

        var seen = Assert.Single(stub.Requests);
        Assert.Equal(HttpMethod.Post, seen.Method);
        Assert.Equal("/v1.0/users/user-1/teamwork/sendActivityNotification", seen.Uri.AbsolutePath);
        var body = JsonNode.Parse(seen.Body!)!.AsObject();
        Assert.Equal("text", body["topic"]!["source"]!.GetValue<string>());
        Assert.Equal("Call completed", body["topic"]!["value"]!.GetValue<string>());
        Assert.Equal("https://teams.microsoft.com/", body["topic"]!["webUrl"]!.GetValue<string>());
        Assert.Equal("callCompleted", body["activityType"]!.GetValue<string>());
        Assert.Equal("Call with user-2", body["previewText"]!["content"]!.GetValue<string>());
        var parameters = body["templateParameters"]!.AsArray();
        Assert.Equal("duration", parameters[0]!["name"]!.GetValue<string>());
        Assert.Equal("61", parameters[0]!["value"]!.GetValue<string>());
        Assert.Equal("peer", parameters[1]!["name"]!.GetValue<string>());
    }

    // ---- options --------------------------------------------------------------------------------------------

    [Fact]
    public void Options_DefaultsAreValidInDemoMode()
    {
        var options = new GraphOptions { TenantId = "11111111-1111-4111-8111-111111111111" };

        Assert.Empty(options.Validate());
        Assert.Equal("https://graph.microsoft.com/v1.0", options.BaseUrl);
        Assert.Equal("https://graph.microsoft.com/.default", options.Scope);
        Assert.Equal(30, options.RequestTimeoutSeconds);
        Assert.Equal(120, options.TotalTimeoutSeconds);
        Assert.Equal(3, options.RetryMaxAttempts);
        Assert.Equal(500, options.RetryBaseDelayMilliseconds);
        Assert.Equal(100, options.MaxPages);
    }

    [Fact]
    public void Options_RejectsBadValues()
    {
        var options = new GraphOptions
        {
            TenantId = "t", BaseUrl = "http://graph.microsoft.com/v1.0", RequestTimeoutSeconds = 0,
            TotalTimeoutSeconds = 0, RetryMaxAttempts = 11, MaxPages = 0,
        };

        var errors = options.Validate().ToList();

        Assert.Contains(errors, e => e.Contains("BaseUrl"));
        Assert.Contains(errors, e => e.Contains("RequestTimeoutSeconds"));
        Assert.Contains(errors, e => e.Contains("RetryMaxAttempts"));
        Assert.Contains(errors, e => e.Contains("MaxPages"));
    }

    [Fact]
    public void Options_LiveModeNeedsClientIdAndSecret()
    {
        var empty = new GraphOptions { TenantId = "11111111-1111-4111-8111-111111111111" };
        Assert.Contains(empty.ValidateLive(), e => e.Contains("ClientId"));
        Assert.Contains(empty.ValidateLive(), e => e.Contains("ClientSecret"));

        Assert.Empty(GraphTestOptions.Valid().ValidateLive());
    }
}
