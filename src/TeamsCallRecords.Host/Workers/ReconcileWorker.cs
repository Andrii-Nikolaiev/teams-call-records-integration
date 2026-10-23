using Microsoft.Extensions.Options;
using TeamsCallRecords.Infrastructure.Config;
using TeamsCallRecords.Infrastructure.Processing;

namespace TeamsCallRecords.Host.Workers;

/// <summary>Every Reconcile:IntervalMinutes: lookback reconciliation and recheck of suspended calls (TRP 3.25).</summary>
public sealed class ReconcileWorker : BackgroundService
{
    private readonly IReconciler _reconciler;
    private readonly IOptions<ReconcileOptions> _options;
    private readonly ReadinessState _readiness;
    private readonly TimeProvider _clock;
    private readonly ILogger<ReconcileWorker> _log;

    public ReconcileWorker(IReconciler reconciler, IOptions<ReconcileOptions> options, ReadinessState readiness,
        TimeProvider clock, ILogger<ReconcileWorker> log)
    {
        _reconciler = reconciler;
        _options = options;
        _readiness = readiness;
        _clock = clock;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await _readiness.WhenReady.WaitAsync(stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceAsync(stoppingToken);
            try { await Task.Delay(TimeSpan.FromMinutes(_options.Value.IntervalMinutes), _clock, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>One cycle; an error in one step does not stop the other or the worker.</summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            var added = await _reconciler.ReconcileLookbackAsync(ct);
            _log.LogInformation("Reconciliation: {Added} call(s) enqueued", added);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) { _log.LogError(e, "Reconciliation failed"); }

        try
        {
            var added = await _reconciler.RecheckSuspendedAsync(ct);
            if (added > 0) _log.LogInformation("Recheck of suspended calls: {Added} enqueued", added);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) { _log.LogError(e, "Recheck of suspended calls failed"); }
    }
}
