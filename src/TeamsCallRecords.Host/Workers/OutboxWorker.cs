using TeamsCallRecords.Infrastructure.Outbox;

namespace TeamsCallRecords.Host.Workers;

/// <summary>Sends queued activity notifications; sleeps 2 s when the queue is empty (TRP 3.25).</summary>
public sealed class OutboxWorker : BackgroundService
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(2);

    private readonly IOutboxSender _sender;
    private readonly ReadinessState _readiness;
    private readonly TimeProvider _clock;
    private readonly ILogger<OutboxWorker> _log;

    public OutboxWorker(IOutboxSender sender, ReadinessState readiness, TimeProvider clock, ILogger<OutboxWorker> log)
    {
        _sender = sender;
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
            try
            {
                if (await _sender.RunOnceAsync(stoppingToken)) continue;
                await Task.Delay(IdleDelay, _clock, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                _log.LogError(e, "Outbox worker error");
                try { await Task.Delay(IdleDelay, _clock, stoppingToken); }
                catch (OperationCanceledException) { return; }
            }
        }
    }
}
