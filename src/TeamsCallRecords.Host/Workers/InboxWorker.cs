using Microsoft.Extensions.Options;
using TeamsCallRecords.Infrastructure.Config;
using TeamsCallRecords.Infrastructure.Processing;
using TeamsCallRecords.Infrastructure.Queue;

namespace TeamsCallRecords.Host.Workers;

/// <summary>Runs Processing:Workers parallel loops: claim, process, renew the lease (TRP 3.25, P-27).</summary>
public sealed class InboxWorker : BackgroundService
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(1);

    private readonly IInboxStore _inbox;
    private readonly ICallProcessor _processor;
    private readonly IOptions<ProcessingOptions> _options;
    private readonly ReadinessState _readiness;
    private readonly TimeProvider _clock;
    private readonly ILogger<InboxWorker> _log;

    public InboxWorker(IInboxStore inbox, ICallProcessor processor, IOptions<ProcessingOptions> options,
        ReadinessState readiness, TimeProvider clock, ILogger<InboxWorker> log)
    {
        _inbox = inbox;
        _processor = processor;
        _options = options;
        _readiness = readiness;
        _clock = clock;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await _readiness.WhenReady.WaitAsync(stoppingToken); }
        catch (OperationCanceledException) { return; }

        var loops = Enumerable.Range(0, _options.Value.Workers).Select(_ => Task.Run(() => LoopAsync(stoppingToken), CancellationToken.None));
        await Task.WhenAll(loops);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var item = await _inbox.ClaimNextAsync(ct);
                if (item == null)
                {
                    await Task.Delay(IdleDelay, _clock, ct);
                    continue;
                }
                await ProcessWithHeartbeatAsync(item, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                _log.LogError(e, "Inbox worker error");
                try { await Task.Delay(IdleDelay, _clock, ct); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private async Task ProcessWithHeartbeatAsync(InboxItem item, CancellationToken ct)
    {
        using var work = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var leaseLost = false;
        var lease = TimeSpan.FromSeconds(_options.Value.LeaseSeconds);
        var expected = item.LockedUntilUtc ?? _clock.GetUtcNow() + lease;

        var heartbeat = Task.Run(async () =>
        {
            try
            {
                while (!work.Token.IsCancellationRequested)
                {
                    await Task.Delay(lease / 3, _clock, work.Token);
                    var renewed = await _inbox.RenewLeaseAsync(item.Id, expected, work.Token);
                    if (renewed == null)
                    {
                        leaseLost = true;
                        _log.LogWarning("Lease lost for inbox item {Id}; processing cancelled", item.Id);
                        work.Cancel();
                        return;
                    }
                    expected = renewed.Value;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                leaseLost = true;
                _log.LogError(e, "Lease renewal failed for inbox item {Id}; processing cancelled", item.Id);
                work.Cancel();
            }
        }, CancellationToken.None);

        try
        {
            var outcome = await _processor.ProcessAsync(item, work.Token);
            _log.LogInformation("Inbox item {Id} ({Kind}, call {CallId}): {Outcome}", item.Id, item.Kind, item.CallId, outcome);
        }
        catch (OperationCanceledException) when (leaseLost && !ct.IsCancellationRequested)
        {
            // another worker owns the item now; it must not be completed here
        }
        finally
        {
            work.Cancel();
            await heartbeat;
        }
    }
}
