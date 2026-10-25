using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using TeamsCallRecords.Infrastructure.Config;
using TeamsCallRecords.Infrastructure.Data.Entities;
using TeamsCallRecords.Infrastructure.Queue;
using TeamsCallRecords.Infrastructure.Subscriptions;

namespace TeamsCallRecords.Host.Web;

/// <summary>
/// POST /graph/notify and POST /graph/lifecycle (TRP 3.10, 3.23). The answer 202 is sent only after the items are
/// committed to the inbox. The clientState is checked and never stored or logged. Each element is classified by
/// <see cref="IGraphNotificationValidator"/>; lifecycle elements (on either route) go to <see cref="ISubscriptionService"/> when
/// subscriptions are managed (ADR-0001 D3).
/// </summary>
public static class GraphWebhookEndpoints
{
    private static readonly Regex CallRecordPath =
        new(@"^/?communications/callRecords/(?<id>[^/?#\s]+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static void MapGraphWebhook(this IEndpointRouteBuilder app)
    {
        app.MapPost("/graph/notify", (HttpContext ctx, IInboxStore inbox, IOptions<WebhookOptions> web, IOptions<GraphOptions> graph,
            IGraphNotificationValidator validator, ISubscriptionService subscriptions, IHostApplicationLifetime lifetime,
            ILoggerFactory logs, CancellationToken ct) =>
            HandleAsync(ctx, inbox, web.Value, graph.Value, validator, subscriptions, lifetime, logs.CreateLogger("Webhook"), lifecycle: false, ct));

        app.MapPost("/graph/lifecycle", (HttpContext ctx, IInboxStore inbox, IOptions<WebhookOptions> web, IOptions<GraphOptions> graph,
            IGraphNotificationValidator validator, ISubscriptionService subscriptions, IHostApplicationLifetime lifetime,
            ILoggerFactory logs, CancellationToken ct) =>
            HandleAsync(ctx, inbox, web.Value, graph.Value, validator, subscriptions, lifetime, logs.CreateLogger("Webhook"), lifecycle: true, ct));
    }

    private static async Task<IResult> HandleAsync(HttpContext ctx, IInboxStore inbox, WebhookOptions web, GraphOptions graph,
        IGraphNotificationValidator validator, ISubscriptionService subscriptions, IHostApplicationLifetime lifetime,
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
            var lifecycleCalls = new List<(string Id, string Event)>();
            int mismatches = 0, ignored = 0, temporary = 0;
            var needReconcile = false;

            foreach (var el in value.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) { ignored++; continue; }

                var subscriptionId = Str(el, "subscriptionId");
                var ev = Str(el, "lifecycleEvent");
                var isLifecycle = lifecycle || ev != null;

                NotificationVerdict verdict;
                try
                {
                    verdict = await validator.ClassifyAsync(subscriptionId, Str(el, "tenantId"), Str(el, "resource"), Str(el, "clientState"),
                        resourceRequired: !isLifecycle, ct);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    log.LogError(e, "Could not check the notification against the subscription store");
                    return Unavailable(ctx, "unavailable", "Could not check the notification, retry later");
                }

                if (verdict == NotificationVerdict.PermanentlyInvalid) { mismatches++; continue; }
                if (verdict == NotificationVerdict.TemporarilyUnknownSubscription) { temporary++; continue; }

                if (isLifecycle)
                {
                    if (!validator.IsManaged)
                    {
                        // Demo / polling mode: there are no subscriptions to maintain; a missed event still triggers a reconciliation.
                        if (string.Equals(ev, "missed", StringComparison.OrdinalIgnoreCase)) needReconcile = true;
                        else log.LogInformation("Lifecycle event {Event} noted (subscriptions are not managed)", ev ?? "(none)");
                    }
                    else if (ev != null && subscriptionId != null) lifecycleCalls.Add((subscriptionId, ev));
                    else ignored++;
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

            var accepted = items.Count + lifecycleCalls.Count;
            if (accepted == 0 && temporary == 0 && mismatches > 0)
            {
                log.LogWarning("Notification rejected: {Mismatches} item(s) failed subscription/clientState/tenant check", mismatches);
                return ApiError.Result(ctx, StatusCodes.Status403Forbidden, "forbidden", "clientState or tenant mismatch");
            }
            if (mismatches > 0) log.LogWarning("{Mismatches} item(s) failed subscription/clientState/tenant check and were dropped", mismatches);
            if (ignored > 0) log.LogInformation("{Ignored} item(s) ignored (not a created callRecord)", ignored);

            if (items.Count > 0)
            {
                InboxEnqueueResult result;
                try { result = await inbox.EnqueueAsync(items, ct); }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    log.LogError(e, "Could not commit notification to the inbox");
                    return Unavailable(ctx, "unavailable", "Could not store the notification, retry later");
                }
                if (result.Overloaded) return Unavailable(ctx, "overloaded", "Inbox is full, retry later");
            }

            foreach (var (id, ev) in lifecycleCalls)
            {
                if (string.Equals(ev, "missed", StringComparison.OrdinalIgnoreCase))
                {
                    // Only writes one inbox item: done before the answer, so a 202 means the reconciliation is queued.
                    try { await subscriptions.HandleLifecycleAsync(id, ev, ct); }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        log.LogError(e, "Could not queue the reconciliation for a missed event");
                        return Unavailable(ctx, "unavailable", "Could not store the notification, retry later");
                    }
                }
                else
                {
                    // Renewal and re-creation call Graph and may take longer than the 3 seconds Graph allows for the answer.
                    // The periodic check (every Subscriptions:RenewCheckMinutes) repairs the subscription if this attempt is lost.
                    RunInBackground(subscriptions, id, ev, lifetime.ApplicationStopping, log);
                }
            }

            // A subscription that Graph has just created is not in the database yet: ask Graph to deliver again. Valid items are already committed.
            if (temporary > 0) return Unavailable(ctx, "subscription_pending", "Subscription is not saved yet, retry later", retryAfterSeconds: 5);
            return Results.StatusCode(StatusCodes.Status202Accepted);
        }
    }

    private static IResult Unavailable(HttpContext ctx, string code, string message, int retryAfterSeconds = 60)
    {
        ctx.Response.Headers.RetryAfter = retryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return ApiError.Result(ctx, StatusCodes.Status503ServiceUnavailable, code, message);
    }

    private static void RunInBackground(ISubscriptionService subscriptions, string id, string lifecycleEvent, CancellationToken stopping, ILogger log) =>
        _ = Task.Run(async () =>
        {
            try { await subscriptions.HandleLifecycleAsync(id, lifecycleEvent, stopping); }
            catch (OperationCanceledException) { /* the application is stopping */ }
            catch (Exception e) { log.LogError(e, "Handling lifecycle event {Event} failed", lifecycleEvent); }
        }, CancellationToken.None);

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
