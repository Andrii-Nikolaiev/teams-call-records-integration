using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TeamsCallRecords.Host.Demo;
using TeamsCallRecords.Infrastructure.Config;
using TeamsCallRecords.Infrastructure.Data;
using TeamsCallRecords.Infrastructure.Data.Entities;
using TeamsCallRecords.Infrastructure.Outbox;
using TeamsCallRecords.Infrastructure.Processing;
using TeamsCallRecords.Infrastructure.Queue;

namespace TeamsCallRecords.Host.Web;

public sealed record PartyDto(string Type, string Id);

public sealed record CdrDto(long CdrSeq, string TenantId, string CallId, string SessionId, long SourceVersion, string CallType,
    PartyDto? Caller, PartyDto? Callee, string StartUtc, string EndUtc, long DurationSeconds, IReadOnlyList<string> Modalities,
    string Status, string? TerminationReason, int SegmentCount, string AggregationStatus, string SourceHash);

public sealed record CdrPageDto(IReadOnlyList<CdrDto> Items, string? Next);

/// <summary>REST interface protected by X-TCRI-Key: GET /api/cdr, POST /api/reprocess, GET /api/status (+ demo endpoints).</summary>
public static class ApiEndpoints
{
    private static readonly TimeSpan MaxCdrRange = TimeSpan.FromDays(90);
    private static readonly TimeSpan MaxReprocessRange = TimeSpan.FromDays(7);
    private const int MaxSmallBody = 4096;

    public static void MapTcriApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").AddEndpointFilter<ApiKeyFilter>();
        api.MapGet("/cdr", GetCdrAsync);
        api.MapPost("/reprocess", ReprocessAsync);
        api.MapGet("/status", GetStatusAsync);
    }

    public static void MapDemo(this IEndpointRouteBuilder app)
    {
        var demo = app.MapGroup("/demo").AddEndpointFilter<ApiKeyFilter>();
        demo.MapPost("/call-records", PutCallRecordAsync);
        demo.MapGet("/notifications", (InMemoryGraphClient graph) => Results.Json(new
        {
            Items = graph.SentNotifications.Select(n => new
            {
                n.RecipientUserId, n.ActivityType, n.TopicText, n.PreviewText, n.TemplateParameters,
            }),
        }));
    }

    // GET /api/cdr?from=...&to=...&limit=...&cursor=...
    private static async Task<IResult> GetCdrAsync(HttpContext ctx, ICdrReader reader, IOptions<ApiOptions> api,
        IOptions<GraphOptions> graph, CancellationToken ct)
    {
        var q = ctx.Request.Query;
        if (!TryUtc(q["from"].ToString(), out var from) || !TryUtc(q["to"].ToString(), out var to))
            return ApiError.Result(ctx, 400, "invalid_parameter", "from and to are required ISO 8601 UTC timestamps, e.g. 2026-10-20T00:00:00Z");
        if (from >= to)
            return ApiError.Result(ctx, 400, "invalid_parameter", "from must be earlier than to");
        if (to - from > MaxCdrRange)
            return ApiError.Result(ctx, 400, "range_too_large", "The range must not exceed 90 days");

        var limit = api.Value.MaxPageSize;
        var limitText = q["limit"].ToString();
        if (limitText.Length > 0
            && (!int.TryParse(limitText, NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit < 1 || limit > api.Value.MaxPageSize))
            return ApiError.Result(ctx, 400, "invalid_parameter", $"limit must be 1..{api.Value.MaxPageSize}");

        var cursor = q["cursor"].ToString();
        try
        {
            var page = await reader.QueryAsync(graph.Value.TenantId, from, to, limit, cursor.Length == 0 ? null : cursor, ct);
            var items = page.Items.Select(i => ToDto(i)).ToList();
            return Results.Json(new CdrPageDto(items, page.Next));
        }
        catch (ArgumentException e) when (e.ParamName == "cursor")
        {
            return ApiError.Result(ctx, 400, "invalid_cursor", "cursor is malformed");
        }
    }

    private static CdrDto ToDto(CdrItem item)
    {
        var r = CdrMapping.ToRow(item.Cdr, "");
        return new CdrDto(item.CdrSeq, r.TenantId, r.CallId, r.SessionId, r.SourceVersion, r.CallType,
            r.CallerType == null ? null : new PartyDto(r.CallerType, r.CallerId ?? ""),
            r.CalleeType == null ? null : new PartyDto(r.CalleeType, r.CalleeId ?? ""),
            r.StartUtc, r.EndUtc, r.DurationS, item.Cdr.Modalities, r.Status, r.TerminationReason, r.SegmentCount,
            r.AggregationStatus, r.SourceHash);
    }

    // POST /api/reprocess {"from_utc","to_utc","force":true}
    private static async Task<IResult> ReprocessAsync(HttpContext ctx, IInboxStore inbox, IOptions<GraphOptions> graph, CancellationToken ct)
    {
        if (!BodyReader.IsJson(ctx.Request))
            return ApiError.Result(ctx, 415, "unsupported_media_type", "Content-Type must be application/json");
        var body = await BodyReader.ReadLimitedAsync(ctx.Request, MaxSmallBody, ct);
        if (body is null) return ApiError.Result(ctx, 413, "body_too_large", "Body is too large");

        DateTimeOffset from, to;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("from_utc", out var f) || f.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("to_utc", out var t) || t.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("force", out var force) || force.ValueKind != JsonValueKind.True
                || !TryUtc(f.GetString(), out from) || !TryUtc(t.GetString(), out to))
                return ApiError.Result(ctx, 400, "invalid_parameter",
                    "Expected {\"from_utc\":\"<ISO UTC>\",\"to_utc\":\"<ISO UTC>\",\"force\":true}");
        }
        catch (JsonException)
        {
            return ApiError.Result(ctx, 400, "bad_json", "Body is not valid JSON");
        }
        if (from >= to) return ApiError.Result(ctx, 400, "invalid_parameter", "from_utc must be earlier than to_utc");
        if (to - from > MaxReprocessRange) return ApiError.Result(ctx, 400, "range_too_large", "The range must not exceed 7 days");

        var raw = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["from_utc"] = UtcText.Format(from), ["to_utc"] = UtcText.Format(to), ["force"] = true,
        });
        var result = await inbox.EnqueueAsync(new[] { new InboxNewItem(InboxKinds.Reconcile, graph.Value.TenantId, null, raw) }, ct);
        if (result.Overloaded)
        {
            ctx.Response.Headers.RetryAfter = "60";
            return ApiError.Result(ctx, 503, "overloaded", "Inbox is full, retry later");
        }
        return Results.Json(new { Queued = result.Added, Duplicates = result.Duplicates }, statusCode: 202);
    }

    // GET /api/status
    private static async Task<IResult> GetStatusAsync(IInboxStore inbox, ISourceStateStore states, IOutboxStore outbox,
        IReconciler reconciler, ReadinessState readiness, TimeProvider clock, CancellationToken ct)
    {
        var i = await inbox.GetStatsAsync(ct);
        var s = await states.GetCountsAsync(ct);
        var o = await outbox.GetStatsAsync(ct);
        return Results.Json(new
        {
            Mode = "demo",
            Ready = readiness.IsReady,
            ServerTimeUtc = UtcText.Format(clock.GetUtcNow()),
            Inbox = new { i.Pending, i.OldestPendingAgeSeconds, i.Failed },
            SourceState = new { s.Pending, s.Suspended, s.Expired },
            Outbox = new { o.Pending, o.Failed },
            Reconcile = new { LastRunUtc = reconciler.LastRunUtc is { } t ? UtcText.Format(t) : null },
        });
    }

    // POST /demo/call-records: puts a callRecord JSON into the in-memory Graph stand-in.
    private static async Task<IResult> PutCallRecordAsync(HttpContext ctx, InMemoryGraphClient graph, CancellationToken ct)
    {
        var body = await BodyReader.ReadLimitedAsync(ctx.Request, 1_048_576, ct);
        if (body is null) return ApiError.Result(ctx, 413, "body_too_large", "Body is too large");
        try
        {
            var json = System.Text.Encoding.UTF8.GetString(body);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(id.GetString()))
                return ApiError.Result(ctx, 400, "invalid_parameter", "callRecord must be an object with a string \"id\"");
            graph.Put(json);
            return Results.Json(new { CallId = id.GetString() }, statusCode: 201);
        }
        catch (JsonException)
        {
            return ApiError.Result(ctx, 400, "bad_json", "Body is not valid JSON");
        }
    }

    /// <summary>ISO 8601 timestamp with an explicit UTC designator or offset; returns UTC.</summary>
    private static bool TryUtc(string? text, out DateTimeOffset value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (!(text.EndsWith('Z') || text.EndsWith('z') || System.Text.RegularExpressions.Regex.IsMatch(text, @"[+-]\d{2}:\d{2}$"))) return false;
        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)) return false;
        value = parsed.ToUniversalTime();
        return true;
    }
}
