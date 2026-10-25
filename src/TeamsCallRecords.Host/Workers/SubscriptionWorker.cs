using Microsoft.Extensions.Options;
using TeamsCallRecords.Infrastructure.Config;
using TeamsCallRecords.Infrastructure.Subscriptions;

namespace TeamsCallRecords.Host.Workers;

/// <summary>
/// Keeps the Graph subscriptions alive: EnsureAsync once at start and then every Subscriptions:RenewCheckMinutes (TRP 3.25, ADR-0001 D4-D6).
/// Works only when Graph:NotificationUrl is set. The first pass waits until the database is ready and the HTTP server is listening,
/// because Graph calls the notification URL while the subscription is being created. A failure here never stops other workers.
/// </summary>
public sealed class SubscriptionWorker : BackgroundService
{
    private readonly ISubscriptionService _service;
    private readonly IOptions<GraphOptions> _graph;
    private readonly IOptions<SubscriptionOptions> _options;
    private readonly ReadinessState _readiness;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly TimeProvider _clock;
    private readonly ILogger<SubscriptionWorker> _log;

    public SubscriptionWorker(ISubscriptionService service, IOptions<GraphOptions> graph, IOptions<SubscriptionOptions> options,
        ReadinessState readiness, IHostApplicationLifetime lifetime, TimeProvider clock, ILogger<SubscriptionWorker> log)
    {
        _service = service;
        _graph = graph;
        _options = options;
        _readiness = readiness;
        _lifetime = lifetime;
        _clock = clock;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_graph.Value.NotificationUrl))
        {
            _log.LogInformation("Graph:NotificationUrl is not set: subscriptions are not managed, only polling is used");
            return;
        }

        try
        {
            await _readiness.WhenReady.WaitAsync(stoppingToken);
            await WaitForServerAsync(stoppingToken);
        }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceAsync(stoppingToken);
            try { await Task.Delay(TimeSpan.FromMinutes(_options.Value.RenewCheckMinutes), _clock, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>One pass; an error is logged and does not stop the worker.</summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            if (await _service.EnsureAsync(ct)) _log.LogInformation("Subscriptions are in order");
            else _log.LogWarning("Subscriptions are not in order, see error_log (component subscriptions); the next check is in {Minutes} min",
                _options.Value.RenewCheckMinutes);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) { _log.LogError(e, "Subscription check failed"); }
    }

    private async Task WaitForServerAsync(CancellationToken ct)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var registration = _lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        await started.Task.WaitAsync(ct);
    }
}
