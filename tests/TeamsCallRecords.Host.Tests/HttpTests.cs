using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace TeamsCallRecords.Host.Tests;

public sealed class HttpTests : IClassFixture<TcriFactory>
{
    private readonly TcriFactory _f;
    public HttpTests(TcriFactory f) => _f = f;

    private static StringContent Json(string s) => new(s, Encoding.UTF8, "application/json");

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage r)
    {
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    private static async Task AssertError(HttpResponseMessage r, HttpStatusCode status, string code)
    {
        Assert.Equal(status, r.StatusCode);
        var body = await ReadAsync(r);
        Assert.Equal(code, body.GetProperty("error").GetProperty("code").GetString());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("trace_id").GetString()));
    }

    [Fact]
    public async Task Health_is_ready()
    {
        var r = await _f.CreateClient().GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task Unknown_path_gives_json_404()
    {
        var r = await _f.CreateClient().GetAsync("/nope");
        await AssertError(r, HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task Notify_echoes_validation_token()
    {
        var r = await _f.CreateClient().PostAsync("/graph/notify?validationToken=abc%20123", null);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.StartsWith("text/plain", r.Content.Headers.ContentType!.ToString());
        Assert.Equal("abc 123", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Notify_rejects_wrong_content_type()
    {
        var r = await _f.CreateClient().PostAsync("/graph/notify", new StringContent("{}", Encoding.UTF8, "text/plain"));
        await AssertError(r, HttpStatusCode.UnsupportedMediaType, "unsupported_media_type");
    }

    [Fact]
    public async Task Notify_rejects_bad_json_and_bad_shape()
    {
        var c = _f.CreateClient();
        await AssertError(await c.PostAsync("/graph/notify", Json("{not json")), HttpStatusCode.BadRequest, "bad_json");
        await AssertError(await c.PostAsync("/graph/notify", Json("{\"x\":1}")), HttpStatusCode.BadRequest, "bad_notification");
    }

    [Fact]
    public async Task Notify_rejects_too_large_batch()
    {
        var items = string.Join(",", Enumerable.Repeat("{}", 101));
        var r = await _f.CreateClient().PostAsync("/graph/notify", Json("{\"value\":[" + items + "]}"));
        await AssertError(r, HttpStatusCode.RequestEntityTooLarge, "batch_too_large");
    }

    [Fact]
    public async Task Notify_with_wrong_client_state_is_forbidden()
    {
        var r = await _f.CreateClient().PostAsync("/graph/notify", Json(Samples.Notification("c-x", clientState: "wrong-wrong-wrong")));
        await AssertError(r, HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public async Task Notify_with_wrong_tenant_is_forbidden()
    {
        var r = await _f.CreateClient().PostAsync("/graph/notify",
            Json(Samples.Notification("c-x", tenant: "22222222-2222-4222-8222-222222222222")));
        await AssertError(r, HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public async Task Notify_for_other_resource_is_accepted_and_ignored()
    {
        var body = Samples.Notification("c-x").Replace("communications/callRecords/c-x", "chats/42");
        var r = await _f.CreateClient().PostAsync("/graph/notify", Json(body));
        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
    }

    [Theory]
    [InlineData("/api/status")]
    [InlineData("/api/cdr?from=2026-01-01T00:00:00Z&to=2026-01-02T00:00:00Z")]
    [InlineData("/demo/notifications")]
    public async Task Api_requires_key(string url)
    {
        var c = _f.CreateClient();
        await AssertError(await c.GetAsync(url), HttpStatusCode.Unauthorized, "unauthorized");

        c.DefaultRequestHeaders.Add("X-TCRI-Key", "wrong-key-wrong-key-1");
        await AssertError(await c.GetAsync(url), HttpStatusCode.Unauthorized, "unauthorized");
    }

    [Theory]
    [InlineData("/api/cdr")]
    [InlineData("/api/cdr?from=2026-01-01T00:00:00Z")]
    [InlineData("/api/cdr?from=2026-01-01&to=2026-01-02")]
    [InlineData("/api/cdr?from=2026-01-02T00:00:00Z&to=2026-01-01T00:00:00Z")]
    [InlineData("/api/cdr?from=2026-01-01T00:00:00Z&to=2026-01-02T00:00:00Z&limit=0")]
    [InlineData("/api/cdr?from=2026-01-01T00:00:00Z&to=2026-01-02T00:00:00Z&limit=101")]
    [InlineData("/api/cdr?from=2026-01-01T00:00:00Z&to=2026-01-02T00:00:00Z&limit=abc")]
    public async Task Cdr_rejects_bad_parameters(string url)
    {
        var r = await _f.ApiClient().GetAsync(url);
        await AssertError(r, HttpStatusCode.BadRequest, "invalid_parameter");
    }

    [Fact]
    public async Task Cdr_rejects_range_over_90_days_and_bad_cursor()
    {
        var c = _f.ApiClient();
        await AssertError(await c.GetAsync("/api/cdr?from=2026-01-01T00:00:00Z&to=2026-06-01T00:00:00Z"),
            HttpStatusCode.BadRequest, "range_too_large");
        await AssertError(await c.GetAsync("/api/cdr?from=2026-01-01T00:00:00Z&to=2026-01-02T00:00:00Z&cursor=!!"),
            HttpStatusCode.BadRequest, "invalid_cursor");
    }

    [Fact]
    public async Task Cdr_on_empty_range_returns_empty_page()
    {
        var r = await _f.ApiClient().GetAsync("/api/cdr?from=2001-01-01T00:00:00Z&to=2001-01-02T00:00:00Z");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await ReadAsync(r);
        Assert.Equal(0, body.GetProperty("items").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("next").ValueKind);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"from_utc\":\"2026-01-01T00:00:00Z\",\"to_utc\":\"2026-01-02T00:00:00Z\"}")]
    [InlineData("{\"from_utc\":\"2026-01-01T00:00:00Z\",\"to_utc\":\"2026-01-02T00:00:00Z\",\"force\":false}")]
    [InlineData("{\"from_utc\":\"2026-01-02T00:00:00Z\",\"to_utc\":\"2026-01-01T00:00:00Z\",\"force\":true}")]
    public async Task Reprocess_rejects_bad_body(string body)
    {
        var r = await _f.ApiClient().PostAsync("/api/reprocess", Json(body));
        await AssertError(r, HttpStatusCode.BadRequest, "invalid_parameter");
    }

    [Fact]
    public async Task Reprocess_rejects_range_over_7_days_and_bad_json()
    {
        var c = _f.ApiClient();
        await AssertError(await c.PostAsync("/api/reprocess",
            Json("{\"from_utc\":\"2026-01-01T00:00:00Z\",\"to_utc\":\"2026-01-10T00:00:00Z\",\"force\":true}")),
            HttpStatusCode.BadRequest, "range_too_large");
        await AssertError(await c.PostAsync("/api/reprocess", Json("{oops")), HttpStatusCode.BadRequest, "bad_json");
    }

    [Fact]
    public async Task Status_reports_state()
    {
        var r = await _f.ApiClient().GetAsync("/api/status");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await ReadAsync(r);
        Assert.Equal("demo", body.GetProperty("mode").GetString());
        Assert.True(body.GetProperty("ready").GetBoolean());
        Assert.True(body.GetProperty("inbox").TryGetProperty("pending", out _));
        Assert.True(body.GetProperty("source_state").TryGetProperty("suspended", out _));
        Assert.True(body.GetProperty("outbox").TryGetProperty("failed", out _));
    }
}
