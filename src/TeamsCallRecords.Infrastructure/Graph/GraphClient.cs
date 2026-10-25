using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using Polly.Timeout;
using TeamsCallRecords.Infrastructure.Config;
using TeamsCallRecords.Infrastructure.Data;

namespace TeamsCallRecords.Infrastructure.Graph;

/// <summary>
/// Microsoft Graph client (TRP 3.14). Retries, timeouts and the circuit breaker live in the HttpClient
/// handler (<see cref="GraphResilienceHandler"/>); this class builds requests, parses responses and classifies errors.
/// </summary>
public sealed class GraphClient : IGraphClient
{
    private const string SessionsLink = "sessions@odata.nextLink";
    private const string SegmentsLink = "segments@odata.nextLink";

    private readonly HttpClient _http;
    private readonly TokenCredential _credential;
    private readonly GraphOptions _options;
    private readonly ILogger<GraphClient> _log;
    private readonly Uri _baseUri;

    public GraphClient(HttpClient http, TokenCredential credential, IOptions<GraphOptions> options, ILogger<GraphClient> log)
    {
        _http = http;
        _credential = credential;
        _options = options.Value;
        _log = log;
        _baseUri = new Uri(_options.BaseUrl.TrimEnd('/') + "/");
        _http.BaseAddress = _baseUri;
    }

    // ---- call records -------------------------------------------------------------------------------------

    public async Task<GraphFetchResult> GetCallRecordAsync(string callId, CancellationToken ct)
    {
        var first = await GetPageAsync(
            $"communications/callRecords/{Uri.EscapeDataString(callId)}?$expand=sessions($expand=segments)", ct, notFoundIsResult: true);
        if (first.Status != GraphFetchStatus.Ok) return new GraphFetchResult(first.Status, first.Text);
        var root = first.Node!;

        // Sessions: follow sessions@odata.nextLink, appending the pages' items to root["sessions"].
        var pages = 1;
        while (NextLink(root, SessionsLink) is { } link)
        {
            if (pages >= _options.MaxPages) return new GraphFetchResult(GraphFetchStatus.PagesIncomplete, root.ToJsonString());
            var page = await GetPageAsync(link, ct, notFoundIsResult: true);
            if (page.Status != GraphFetchStatus.Ok) return new GraphFetchResult(page.Status, page.Text);
            pages++;
            AppendValues(root, "sessions", page.Node!);
            SetNextLink(root, SessionsLink, NextLink(page.Node!, "@odata.nextLink"));
        }

        // Segments: the same for every session; MaxPages applies to each collection separately.
        if (root["sessions"] is JsonArray sessions)
        {
            foreach (var node in sessions)
            {
                if (node is not JsonObject session) continue;
                pages = 1;
                while (NextLink(session, SegmentsLink) is { } link)
                {
                    if (pages >= _options.MaxPages) return new GraphFetchResult(GraphFetchStatus.PagesIncomplete, root.ToJsonString());
                    var page = await GetPageAsync(link, ct, notFoundIsResult: true);
                    if (page.Status != GraphFetchStatus.Ok) return new GraphFetchResult(page.Status, page.Text);
                    pages++;
                    AppendValues(session, "segments", page.Node!);
                    SetNextLink(session, SegmentsLink, NextLink(page.Node!, "@odata.nextLink"));
                }
            }
        }

        root.Remove(SessionsLink);
        if (root["sessions"] is JsonArray all)
            foreach (var node in all)
                if (node is JsonObject s) s.Remove(SegmentsLink);

        return new GraphFetchResult(GraphFetchStatus.Ok, root.ToJsonString());
    }

    // [P-proposed] The $filter on startDateTime has not been verified against a live tenant yet.
    public async Task<IReadOnlyList<GraphCallRecordRef>> ListCallRecordsAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        var filter = $"startDateTime ge {UtcText.Format(fromUtc)} and startDateTime lt {UtcText.Format(toUtc)}";
        var result = new List<GraphCallRecordRef>();
        string? target = $"communications/callRecords?$filter={Uri.EscapeDataString(filter)}";
        var pages = 0;

        while (target is not null)
        {
            var page = await GetPageAsync(target, ct, notFoundIsResult: false);
            RequireJson(page, HttpMethod.Get, target);
            pages++;

            if (page.Node!["value"] is JsonArray items)
            {
                foreach (var item in items)
                {
                    var id = GetString(item, "id");
                    if (string.IsNullOrEmpty(id))
                    {
                        _log.LogWarning("Graph returned a callRecord without id; skipped");
                        continue;
                    }
                    result.Add(new GraphCallRecordRef(id, GetLong(item, "version"), GetTime(item, "startDateTime")));
                }
            }

            target = NextLink(page.Node!, "@odata.nextLink");
            if (target is not null && pages >= _options.MaxPages)
                throw new GraphException($"GET {PathOf("communications/callRecords")}: more than {_options.MaxPages} pages", null, false);
        }
        return result;
    }

    // ---- subscriptions ------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<GraphSubscriptionInfo>> ListSubscriptionsAsync(CancellationToken ct)
    {
        var result = new List<GraphSubscriptionInfo>();
        string? target = "subscriptions";
        var pages = 0;

        while (target is not null)
        {
            var page = await GetPageAsync(target, ct, notFoundIsResult: false);
            RequireJson(page, HttpMethod.Get, target);
            pages++;

            if (page.Node!["value"] is JsonArray items)
            {
                foreach (var item in items)
                {
                    var id = GetString(item, "id");
                    var resource = GetString(item, "resource");
                    var url = GetString(item, "notificationUrl");
                    var expires = GetTime(item, "expirationDateTime");
                    if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(resource) || string.IsNullOrEmpty(url) || expires is null)
                    {
                        _log.LogWarning("Graph returned a subscription with missing fields; skipped");
                        continue;
                    }
                    result.Add(new GraphSubscriptionInfo(id, resource, url, expires.Value));
                }
            }

            target = NextLink(page.Node!, "@odata.nextLink");
            if (target is not null && pages >= _options.MaxPages)
                throw new GraphException($"GET {PathOf("subscriptions")}: more than {_options.MaxPages} pages", null, false);
        }
        return result;
    }

    // [P-proposed] Request body; the subscription lifetime limit has not been verified in a tenant yet.
    public async Task<GraphSubscription> CreateSubscriptionAsync(GraphSubscriptionRequest request, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["changeType"] = "created",
            ["notificationUrl"] = request.NotificationUrl,
            ["lifecycleNotificationUrl"] = request.NotificationUrl,
            ["resource"] = request.Resource,
            ["expirationDateTime"] = UtcText.Format(request.ExpiresUtc),
            ["clientState"] = request.ClientState,
        };
        var json = await SendJsonAsync(HttpMethod.Post, "subscriptions", body, ct);
        return ReadSubscription(json, HttpMethod.Post, "subscriptions", requireResource: true);
    }

    public async Task<GraphSubscription> RenewSubscriptionAsync(string subscriptionId, DateTimeOffset newExpiresUtc, CancellationToken ct)
    {
        var target = $"subscriptions/{Uri.EscapeDataString(subscriptionId)}";
        var body = new JsonObject { ["expirationDateTime"] = UtcText.Format(newExpiresUtc) };
        var json = await SendJsonAsync(HttpMethod.Patch, target, body, ct);
        return ReadSubscription(json, HttpMethod.Patch, target, requireResource: false);
    }

    public async Task DeleteSubscriptionAsync(string subscriptionId, CancellationToken ct)
    {
        var target = $"subscriptions/{Uri.EscapeDataString(subscriptionId)}";
        using var response = await SendAsync(HttpMethod.Delete, target, null, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return;   // already absent
        ThrowIfError(response, HttpMethod.Delete, target);
    }

    // [P-proposed] Request body; the activity type must be declared in the Teams app manifest.
    public async Task SendActivityNotificationAsync(ActivityNotification n, CancellationToken ct)
    {
        var parameters = new JsonArray();
        foreach (var (key, value) in n.TemplateParameters)
            parameters.Add(new JsonObject { ["name"] = key, ["value"] = value });

        var body = new JsonObject
        {
            ["topic"] = new JsonObject { ["source"] = "text", ["value"] = n.TopicText, ["webUrl"] = _options.ActivityTopicWebUrl },
            ["activityType"] = n.ActivityType,
            ["previewText"] = new JsonObject { ["content"] = n.PreviewText },
            ["templateParameters"] = parameters,
        };
        var target = $"users/{Uri.EscapeDataString(n.RecipientUserId)}/teamwork/sendActivityNotification";
        using var response = await SendAsync(HttpMethod.Post, target, body, ct);
        ThrowIfError(response, HttpMethod.Post, target);
    }

    // ---- transport ----------------------------------------------------------------------------------------

    private sealed record Page(GraphFetchStatus Status, JsonObject? Node, string? Text);

    /// <summary>GET a page and parse it as a JSON object. Non-JSON text is returned as Malformed.</summary>
    private async Task<Page> GetPageAsync(string target, CancellationToken ct, bool notFoundIsResult)
    {
        using var response = await SendAsync(HttpMethod.Get, target, null, ct);
        if (notFoundIsResult && response.StatusCode == HttpStatusCode.NotFound)
            return new Page(GraphFetchStatus.NotFound, null, null);
        ThrowIfError(response, HttpMethod.Get, target);

        var text = await ReadBodyAsync(response, HttpMethod.Get, target, ct);
        return TryParseObject(text, out var node)
            ? new Page(GraphFetchStatus.Ok, node, text)
            : new Page(GraphFetchStatus.Malformed, null, text);
    }

    /// <summary>POST/PATCH with a JSON body; a 2xx response must be a JSON object.</summary>
    private async Task<JsonObject> SendJsonAsync(HttpMethod method, string target, JsonNode body, CancellationToken ct)
    {
        using var response = await SendAsync(method, target, body, ct);
        ThrowIfError(response, method, target);
        var text = await ReadBodyAsync(response, method, target, ct);
        if (!TryParseObject(text, out var node))
            throw new GraphException($"{method} {PathOf(target)}: response is not valid JSON", null, false);
        return node!;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string target, JsonNode? body, CancellationToken ct)
    {
        var uri = Resolve(target, method);
        var isPost = method == HttpMethod.Post;

        AccessToken token;
        try
        {
            token = await _credential.GetTokenAsync(new TokenRequestContext(new[] { _options.Scope }), ct).ConfigureAwait(false);
        }
        catch (AuthenticationFailedException e)
        {
            throw new GraphException("token acquisition failed", null, false, e);
        }

        using var request = new HttpRequestMessage(method, uri);
        // The token goes only to the host of BaseUrl (Resolve has already refused any other host).
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null)
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        try
        {
            return await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        }
        catch (BrokenCircuitException e)
        {
            throw new GraphException($"{method} {uri.AbsolutePath}: circuit breaker is open, request not sent", null, true, e);
        }
        catch (Exception e) when (!ct.IsCancellationRequested
            && e is HttpRequestException or TimeoutRejectedException or OperationCanceledException)
        {
            throw new GraphException($"{method} {uri.AbsolutePath}: network error or timeout", null, true, e, outcomeUnknown: isPost);
        }
    }

    private async Task<string> ReadBodyAsync(HttpResponseMessage response, HttpMethod method, string target, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (!ct.IsCancellationRequested && e is HttpRequestException or IOException)
        {
            throw new GraphException($"{method} {PathOf(target)}: response body could not be read", null, true, e,
                outcomeUnknown: method == HttpMethod.Post);
        }
    }

    /// <summary>Error classification (TRP 3.14.1). Messages never include the response body or the token.</summary>
    private void ThrowIfError(HttpResponseMessage response, HttpMethod method, string target)
    {
        if (response.IsSuccessStatusCode) return;

        var code = (int)response.StatusCode;
        var isPost = method == HttpMethod.Post;
        var message = $"{method} {PathOf(target)} returned {code}";

        if (code == 429) throw new GraphException(message, code, true);
        if (code == 408 || code >= 500) throw new GraphException(message, code, true, outcomeUnknown: isPost);
        throw new GraphException(message, code, false);
    }

    private void RequireJson(Page page, HttpMethod method, string target)
    {
        if (page.Status != GraphFetchStatus.Ok)
            throw new GraphException($"{method} {PathOf(target)}: response is not valid JSON", null, false);
    }

    /// <summary>Relative addresses are resolved against BaseUrl; absolute ones (continuation links) must have its host.</summary>
    private Uri Resolve(string target, HttpMethod method)
    {
        var uri = Uri.TryCreate(target, UriKind.Absolute, out var absolute) ? absolute : new Uri(_baseUri, target);
        if (!string.Equals(uri.Host, _baseUri.Host, StringComparison.OrdinalIgnoreCase)
            || uri.Scheme != _baseUri.Scheme || uri.Port != _baseUri.Port)
            throw new GraphException($"{method} {uri.AbsolutePath}: address has a different host than BaseUrl", null, false);
        return uri;
    }

    private string PathOf(string target) => Resolve(target, HttpMethod.Get).AbsolutePath;

    // ---- JSON helpers -------------------------------------------------------------------------------------

    private static bool TryParseObject(string text, out JsonObject? node)
    {
        node = null;
        try
        {
            node = JsonNode.Parse(text) as JsonObject;
            return node is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? NextLink(JsonObject node, string name) =>
        node[name] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrEmpty(s) ? s : null;

    private static void SetNextLink(JsonObject node, string name, string? link)
    {
        if (link is null) node.Remove(name);
        else node[name] = link;
    }

    private static void AppendValues(JsonObject target, string property, JsonObject page)
    {
        if (target[property] is not JsonArray array)
        {
            array = new JsonArray();
            target[property] = array;
        }
        if (page["value"] is JsonArray values)
            foreach (var item in values)
                array.Add(item?.DeepClone());
    }

    private static string? GetString(JsonNode? node, string name) =>
        node is JsonObject o && o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static long? GetLong(JsonNode? node, string name) =>
        node is JsonObject o && o[name] is JsonValue v && v.TryGetValue<long>(out var n) ? n : null;

    private static DateTimeOffset? GetTime(JsonNode? node, string name) =>
        GetString(node, name) is { } s
        && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t)
            ? t : null;

    private static GraphSubscription ReadSubscription(JsonObject json, HttpMethod method, string target, bool requireResource)
    {
        var id = GetString(json, "id");
        var expires = GetTime(json, "expirationDateTime");
        var resource = GetString(json, "resource");
        if (string.IsNullOrEmpty(id) || expires is null || (requireResource && string.IsNullOrEmpty(resource)))
            throw new GraphException($"{method} {target.Split('?')[0]}: response has no subscription fields", null, false);
        return new GraphSubscription(id, resource ?? "", expires.Value);
    }
}
