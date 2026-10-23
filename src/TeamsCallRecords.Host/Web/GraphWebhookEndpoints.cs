using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using TeamsCallRecords.Infrastructure.Config;
using TeamsCallRecords.Infrastructure.Data.Entities;
using TeamsCallRecords.Infrastructure.Queue;

namespace TeamsCallRecords.Host.Web;

/// <summary>
/// POST /graph/notify and POST /graph/lifecycle (TRP 3.10). The answer 202 is sent only after the items are
/// committed to the inbox. The clientState is checked and never stored or logged.
/// </summary>
public static class GraphWebhookEndpoints
{
    private static readonly Regex CallRecordPath =
        new(@"^/?communications/callRecords/(?<id>[^/?#\s]+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static void MapGraphWebhook(this IEndpointRouteBuilder app)
    {
        app.MapPost("/graph/notify", (HttpContext ctx, IInboxStore inbox, IOptions<WebhookOptions> web,
            IOptions<GraphOptions> graph, ILoggerFactory logs, CancellationToken ct) =>
            HandleAsync(ctx, inbox, web.Value, graph.Value, logs.CreateLogger("Webhook"), lifecycle: false, ct));

        app.MapPost("/graph/lifecycle", (HttpContext ctx, IInboxStore inbox, IOptions<WebhookOptions> web,
            IOptions<GraphOptions> graph, ILoggerFactory logs, CancellationToken ct) =>
            HandleAsync(ctx, inbox, web.Value, graph.Value, logs.CreateLogger("Webhook"), lifecycle: true, ct));
    }

    private static async Task<IResult> HandleAsync(HttpContext ctx, IInboxStore inbox, WebhookOptions web, GraphOptions graph,
        ILogger log, bool lifecycle, CancellationToken ct)
    {
        // Subscription validation handshake: echo the token as text/plain within 10 seconds.
        if (ctx.Request.Query.TryGetValue("validationToken", out var token) && token.Count > 0 && token[0]!.Length > 0)
            return Results.Text(token[0]!, "text/plain", System.Text.Encoding.UTF8);

        if (!BodyReader.IsJson(ctx.Request))
            return ApiError.Result(ctx, StatusCodes.Status415UnsupportedMediaType, "unsupported_media_type", "Content-Type must be application/json");

        var body = await BodyReader.ReadLimitedAsync(ctx.Request, web.MaxBodyBytes, ct);
        if (body is null)
            return ApiError.Result(ctx, StatusCodes.Status413PayloadTooLarge, "body_too_large", $"Body is larger than {web.MaxBodyBytes} bytes");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); }
        catch (JsonException) { return ApiError.Result(ctx, StatusCodes.Status400BadRequest, "bad_json", "Body is not valid JSON"); }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Array)
                return ApiError.Result(ctx, StatusCodes.Status400BadRequest, "bad_notification", "Expected an object with a \"value\" array");

            if (value.GetArrayLength() > web.MaxBatchItems)
                return ApiError.Result(ctx, StatusCodes.Status413PayloadTooLarge, "batch_too_large", $"More than {web.MaxBatchItems} items");

            var items = new List<InboxNewItem>();
            int mismatches = 0, ignored = 0;
            var needReconcile = false;

            foreach (var el in value.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) { ignored++; continue; }

                var clientState = Str(el, "clientState");
                var tenant = Str(el, "tenantId");
                if (!SecretCompare.Equal(clientState, web.ClientState)
                    || (tenant != null && !string.Equals(tenant, graph.TenantId, StringComparison.OrdinalIgnoreCase)))
                { mismatches++; continue; }

                if (lifecycle)
                {
                    var ev = Str(el, "lifecycleEvent");
                    if (string.Equals(ev, "missed", StringComparison.OrdinalIgnoreCase)) needReconcile = true;
                    else log.LogInformation("Lifecycle event {Event} noted (no subscription store in demo mode)", ev ?? "(none)");
                    continue;
                }

                var change = Str(el, "changeType");
                var resource = Str(el, "resource");
                var m = resource == null ? null : CallRecordPath.Match(resource);
                if (!string.Equals(change, "created", StringComparison.OrdinalIgnoreCase) || m is not { Success: true })
                { ignored++; continue; }

                items.Add(new InboxNewItem(InboxKinds.CallRecord, graph.TenantId, m.Groups["id"].Value, WithoutClientState(el)));
            }

            if (needReconcile)
                items.Add(new InboxNewItem(InboxKinds.Reconcile, graph.TenantId, null, "{}"));

            if (items.Count == 0 && mismatches > 0)
            {
                log.LogWarning("Notification rejected: {Mismatches} item(s) failed clientState/tenant check", mismatches);
                return ApiError.Result(ctx, StatusCodes.Status403Forbidden, "forbidden", "clientState or tenant mismatch");
            }
            if (mismatches > 0) log.LogWarning("{Mismatches} item(s) failed clientState/tenant check and were dropped", mismatches);
            if (ignored > 0) log.LogInformation("{Ignored} item(s) ignored (not a created callRecord)", ignored);

            if (items.Count > 0)
            {
                InboxEnqueueResult result;
                try { result = await inbox.EnqueueAsync(items, ct); }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    log.LogError(e, "Could not commit notification to the inbox");
                    ctx.Response.Headers.RetryAfter = "60";
                    return ApiError.Result(ctx, StatusCodes.Status503ServiceUnavailable, "unavailable", "Could not store the notification, retry later");
                }
                if (result.Overloaded)
                {
                    ctx.Response.Headers.RetryAfter = "60";
                    return ApiError.Result(ctx, StatusCodes.Status503ServiceUnavailable, "overloaded", "Inbox is full, retry later");
                }
            }
            return Results.StatusCode(StatusCodes.Status202Accepted);
        }
    }

    private static string? Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    /// <summary>Copy of the notification without the secret clientState.</summary>
    internal static string WithoutClientState(JsonElement el)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            foreach (var p in el.EnumerateObject())
            {
                if (p.NameEquals("clientState")) continue;
                p.WriteTo(w);
            }
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }
}
