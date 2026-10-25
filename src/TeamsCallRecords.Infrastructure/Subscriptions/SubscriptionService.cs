using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TeamsCallRecords.Infrastructure.Config;
using TeamsCallRecords.Infrastructure.Data;
using TeamsCallRecords.Infrastructure.Data.Entities;
using TeamsCallRecords.Infrastructure.Diagnostics;
using TeamsCallRecords.Infrastructure.Graph;
using TeamsCallRecords.Infrastructure.Queue;

namespace TeamsCallRecords.Infrastructure.Subscriptions;

/// <summary>
/// Subscription creation, renewal and lifecycle handling (TRP 3.15.3, ADR-0001 DV-1 and DV-2).
/// Operations that change subscriptions are serialized by one lock, so the periodic check and a lifecycle
/// notification cannot create the same subscription twice (Graph answers 409 to a duplicate).
/// </summary>
public sealed class SubscriptionService : ISubscriptionService
{
    private const string EventReauthorizationRequired = "reauthorizationRequired";
    private const string EventSubscriptionRemoved = "subscriptionRemoved";
    private const string EventMissed = "missed";

    private readonly IGraphClient _graph;
    private readonly ISubscriptionStore _store;
    private readonly IInboxStore _inbox;
    private readonly IErrorLog _errors;
    private readonly IOptions<GraphOptions> _graphOptions;
    private readonly IOptions<SubscriptionOptions> _options;
    private readonly IPendingSubscriptionMarker _marker;
    private readonly TimeProvider _clock;
    private readonly ILogger<SubscriptionService> _log;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public SubscriptionService(IGraphClient graph, ISubscriptionStore store, IInboxStore inbox, IErrorLog errors,
        IOptions<GraphOptions> graphOptions, IOptions<SubscriptionOptions> options, IPendingSubscriptionMarker marker,
        TimeProvider clock, ILogger<SubscriptionService> log)
    {
        _graph = graph;
        _store = store;
        _inbox = inbox;
        _errors = errors;
        _graphOptions = graphOptions;
        _options = options;
        _marker = marker;
        _clock = clock;
        _log = log;
    }

    private sealed record Desired(string Kind, string Resource, TimeSpan Lifetime);

    private List<Desired> DesiredList()
    {
        var o = _options.Value;
        var list = new List<Desired>
        {
            new(SubscriptionKinds.CallRecords, "communications/callRecords", TimeSpan.FromMinutes(o.CallRecordsLifetimeMinutes)),
        };
        if (!string.IsNullOrWhiteSpace(o.TeamId) && !string.IsNullOrWhiteSpace(o.ChannelId))
            list.Add(new(SubscriptionKinds.ChannelMessages, $"teams/{o.TeamId}/channels/{o.ChannelId}/messages",
                TimeSpan.FromMinutes(o.ChannelMessagesLifetimeMinutes)));
        return list;
    }

    // ---- Ensure -------------------------------------------------------------------------------------------

    public async Task<bool> EnsureAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var rows = await _store.ListAsync(ct);
            var allOk = true;
            foreach (var d in DesiredList())
            {
                try
                {
                    if (!await EnsureOneAsync(d, rows, ct)) allOk = false;
                }
                catch (GraphException ex)
                {
                    allOk = false;
                    await _errors.WriteAsync(Components.Subscriptions, "subscription ensure failed", Describe(d, ex), ct);
                }
            }
            return allOk;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<bool> EnsureOneAsync(Desired d, IReadOnlyList<SubscriptionRow> rows, CancellationToken ct)
    {
        var url = _graphOptions.Value.NotificationUrl;
        var now = _clock.GetUtcNow();
        var mine = rows.Where(r => r.Kind == d.Kind).ToList();

        // Rows that could serve notifications right now.
        var live = mine.Where(r => (r.Status == SubscriptionStatuses.Active || r.Status == SubscriptionStatuses.Renewing)
                                   && SameResource(r.Resource, d.Resource)
                                   && UtcText.Parse(r.ExpiresUtc) > now).ToList();
        var matching = live.Where(r => string.Equals(r.NotificationUrl, url, StringComparison.Ordinal)).ToList();
        var foreign = live.Except(matching).ToList();
        var best = matching.OrderByDescending(r => UtcText.Parse(r.ExpiresUtc)).ThenBy(r => r.Id, StringComparer.Ordinal).FirstOrDefault();

        // ADR-0001 DV-1: the notification URL changed. Nothing is created or deleted before the owner decides.
        if (best == null && foreign.Count > 0)
        {
            await _errors.WriteAsync(Components.Subscriptions, "subscription notification URL mismatch",
                $"{d.Kind}: stored subscription {foreign[0].Id} uses {foreign[0].NotificationUrl}, configured {url}", ct);
            return false;
        }

        foreach (var row in mine.Where(r => r != best && !foreign.Contains(r)))
        {
            await DeleteInGraphQuietlyAsync(row.Id, ct);
            await _store.DeleteAsync(row.Id, ct);
        }

        if (best == null) return await CreateAsync(d, ct) != null;
        if (UtcText.Parse(best.ExpiresUtc) - now < d.Lifetime / 2) return await RenewOrReplaceAsync(best, d, ct);
        return true;
    }

    /// <summary>
    /// Creates the subscription, or adopts one that already exists in Graph (created earlier, response or row lost).
    /// Returns the id of the stored row, or null when the outcome of the POST is unknown (logged; the next pass discovers it).
    /// </summary>
    private async Task<string?> CreateAsync(Desired d, CancellationToken ct)
    {
        var g = _graphOptions.Value;
        var now = _clock.GetUtcNow();
        var hash = SubscriptionStore.HashClientState(g.ClientState);

        var known = await _graph.ListSubscriptionsAsync(ct);
        var found = known
            .Where(k => SameResource(k.Resource, d.Resource)
                        && string.Equals(k.NotificationUrl, g.NotificationUrl, StringComparison.Ordinal)
                        && k.ExpiresUtc > now)
            .OrderByDescending(k => k.ExpiresUtc).ThenBy(k => k.Id, StringComparer.Ordinal)
            .ToList();
        if (found.Count > 0)
        {
            var keep = found[0];
            foreach (var extra in found.Skip(1))
            {
                await DeleteInGraphQuietlyAsync(extra.Id, ct);
                await _store.DeleteAsync(extra.Id, ct);
            }
            await _store.UpsertAsync(NewRow(d, keep.Id, keep.ExpiresUtc, now, hash, g.NotificationUrl), ct);
            _log.LogInformation("Adopted existing {Kind} subscription {Id}", d.Kind, keep.Id);
            return keep.Id;
        }

        GraphSubscription created;
        try
        {
            created = await _graph.CreateSubscriptionAsync(
                new GraphSubscriptionRequest(d.Kind, d.Resource, g.NotificationUrl, now + d.Lifetime, g.ClientState), ct);
        }
        catch (GraphException ex) when (ex.OutcomeUnknown)
        {
            await _errors.WriteAsync(Components.Subscriptions, "subscription create outcome unknown", Describe(d, ex), ct);
            return null;
        }

        // ADR-0001 DV-2: notifications may arrive before the row is saved; the webhook trusts the marker for a short time.
        _marker.Mark(created.Id, g.TenantId, d.Resource, hash);
        await _store.UpsertAsync(NewRow(d, created.Id, created.ExpiresUtc, now, hash, g.NotificationUrl), ct);
        _marker.Clear(created.Id);
        _log.LogInformation("Created {Kind} subscription {Id}", d.Kind, created.Id);
        return created.Id;
    }

    /// <summary>Renews the row; when Graph refuses, creates a new subscription and removes the old one. Returns true when a usable row exists afterwards.</summary>
    private async Task<bool> RenewOrReplaceAsync(SubscriptionRow row, Desired d, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        try
        {
            var renewed = await _graph.RenewSubscriptionAsync(row.Id, now + d.Lifetime, ct);
            await _store.UpsertAsync(new SubscriptionRow
            {
                Id = row.Id,
                Kind = row.Kind,
                Resource = row.Resource,
                NotificationUrl = row.NotificationUrl,
                ExpiresUtc = UtcText.Format(renewed.ExpiresUtc),
                ClientStateHash = row.ClientStateHash,
                Status = SubscriptionStatuses.Active,
                CreatedUtc = row.CreatedUtc,
                RenewedUtc = UtcText.Format(now),
            }, ct);
            return true;
        }
        catch (GraphException ex)
        {
            await _errors.WriteAsync(Components.Subscriptions, "subscription renewal failed", Describe(d, ex) + $"; id {row.Id}", ct);
        }

        await _store.SetStatusAsync(row.Id, SubscriptionStatuses.Failed, ct);
        var newId = await CreateAsync(d, ct);
        if (newId == null) return false;

        // The old subscription may have been adopted again by Create (it still exists in Graph): then it must stay.
        if (!string.Equals(newId, row.Id, StringComparison.Ordinal))
        {
            await DeleteInGraphQuietlyAsync(row.Id, ct);
            await _store.DeleteAsync(row.Id, ct);
        }
        return true;
    }

    // ---- Lifecycle ----------------------------------------------------------------------------------------

    public async Task HandleLifecycleAsync(string subscriptionId, string lifecycleEvent, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var row = await _store.GetAsync(subscriptionId, ct);
            if (row == null)
            {
                _log.LogWarning("Lifecycle event {Event} for unknown subscription {Id}", lifecycleEvent, subscriptionId);
                return;
            }

            switch (lifecycleEvent)
            {
                case EventReauthorizationRequired:
                case EventSubscriptionRemoved:
                    await HandleRenewalEventAsync(row, lifecycleEvent, ct);
                    break;
                case EventMissed:
                    await EnqueueFullReconcileAsync(subscriptionId, ct);
                    break;
                default:
                    _log.LogWarning("Unknown lifecycle event {Event} for subscription {Id}", lifecycleEvent, subscriptionId);
                    break;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task HandleRenewalEventAsync(SubscriptionRow row, string lifecycleEvent, CancellationToken ct)
    {
        var d = DesiredList().FirstOrDefault(x => x.Kind == row.Kind);
        if (d == null)
        {
            _log.LogWarning("Lifecycle event {Event} for subscription {Id} of a disabled kind {Kind}", lifecycleEvent, row.Id, row.Kind);
            if (lifecycleEvent == EventSubscriptionRemoved) await _store.SetStatusAsync(row.Id, SubscriptionStatuses.Failed, ct);
            return;
        }

        try
        {
            if (lifecycleEvent == EventReauthorizationRequired)
            {
                await RenewOrReplaceAsync(row, d, ct);
                return;
            }

            await _store.SetStatusAsync(row.Id, SubscriptionStatuses.Failed, ct);
            var newId = await CreateAsync(d, ct);
            if (newId != null && !string.Equals(newId, row.Id, StringComparison.Ordinal))
                await _store.DeleteAsync(row.Id, ct);
        }
        catch (GraphException ex)
        {
            await _errors.WriteAsync(Components.Subscriptions, "subscription lifecycle handling failed",
                Describe(d, ex) + $"; event {lifecycleEvent}; id {row.Id}", ct);
        }
    }

    private async Task EnqueueFullReconcileAsync(string subscriptionId, CancellationToken ct)
    {
        var raw = JsonSerializer.Serialize(new
        {
            reason = "missed",
            subscription_id = subscriptionId,
            at = UtcText.Format(_clock.GetUtcNow()),
        });
        await _inbox.EnqueueAsync(new[] { new InboxNewItem(InboxKinds.Reconcile, _graphOptions.Value.TenantId, null, raw) }, ct);
    }

    // ---- helpers ------------------------------------------------------------------------------------------

    private async Task DeleteInGraphQuietlyAsync(string id, CancellationToken ct)
    {
        try
        {
            await _graph.DeleteSubscriptionAsync(id, ct);
        }
        catch (GraphException ex)
        {
            _log.LogWarning("Deleting subscription {Id} in Graph failed: {Message}", id, ex.Message);
        }
    }

    private static SubscriptionRow NewRow(Desired d, string id, DateTimeOffset expires, DateTimeOffset now, string hash, string url) => new()
    {
        Id = id,
        Kind = d.Kind,
        Resource = d.Resource,
        NotificationUrl = url,
        ExpiresUtc = UtcText.Format(expires),
        ClientStateHash = hash,
        Status = SubscriptionStatuses.Active,
        CreatedUtc = UtcText.Format(now),
        RenewedUtc = null,
    };

    private static bool SameResource(string a, string b) =>
        string.Equals(a.Trim('/'), b.Trim('/'), StringComparison.OrdinalIgnoreCase);

    private static string Describe(Desired d, GraphException ex) =>
        $"{d.Kind}: {ex.Message}" + (ex.StatusCode is { } code ? $" (HTTP {code})" : "");
}
