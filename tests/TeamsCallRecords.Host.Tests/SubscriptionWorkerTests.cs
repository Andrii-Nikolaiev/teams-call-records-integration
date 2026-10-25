using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TeamsCallRecords.Host.Workers;
using TeamsCallRecords.Infrastructure.Config;
using TeamsCallRecords.Infrastructure.Processing;
using TeamsCallRecords.Infrastructure.Subscriptions;

namespace TeamsCallRecords.Host.Tests;

public sealed class SubscriptionWorkerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Time that moves only when the test says so; timers fire inside Advance.</summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = new();
        private DateTimeOffset _now = T0;

        public override DateTimeOffset GetUtcNow() { lock (_gate) return _now; }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            lock (_gate) _timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }

        public void Advance(TimeSpan by)
        {
            DateTimeOffset target;
            lock (_gate) target = _now + by;
            while (true)
            {
                ManualTimer? due;
                lock (_gate)
                {
                    due = _timers.Where(t => t.Due != null && t.Due <= target).OrderBy(t => t.Due).FirstOrDefault();
                    if (due == null) { _now = target; return; }
                    if (due.Due!.Value > _now) _now = due.Due.Value;
                }
                due.Fire();
            }
        }

        private void Remove(ManualTimer timer) { lock (_gate) _timers.Remove(timer); }

        private sealed class ManualTimer : ITimer
        {
            private readonly ManualTimeProvider _owner;
            private readonly TimerCallback _callback;
            private readonly object? _state;
            private TimeSpan _period = Timeout.InfiniteTimeSpan;

            public ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state)
            {
                _owner = owner;
                _callback = callback;
                _state = state;
            }

            public DateTimeOffset? Due { get; private set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (_owner._gate)
                {
                    Due = dueTime == Timeout.InfiniteTimeSpan ? null : _owner._now + dueTime;
                    _period = period;
                }
                return true;
            }

            public void Fire()
            {
                lock (_owner._gate)
                    Due = _period > TimeSpan.Zero && _period != Timeout.InfiniteTimeSpan ? Due + _period : null;
                _callback(_state);
            }

            public void Dispose() { Due = null; _owner.Remove(this); }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class TestLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();
        public CancellationToken ApplicationStarted => _started.Token;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => _stopped.Token;
        public void StopApplication() => _stopping.Cancel();
        public void Started() => _started.Cancel();
    }

    private sealed class CountingService : ISubscriptionService
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public Func<int, Exception?> Throws { get; set; } = _ => null;
        public Func<int, bool> Result { get; set; } = _ => true;

        public Task<bool> EnsureAsync(CancellationToken ct)
        {
            var n = Interlocked.Increment(ref _calls);
            var ex = Throws(n);
            if (ex != null) throw ex;
            return Task.FromResult(Result(n));
        }

        public Task HandleLifecycleAsync(string subscriptionId, string lifecycleEvent, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class CountingReconciler : IReconciler
    {
        private int _lookbacks;
        public int Lookbacks => Volatile.Read(ref _lookbacks);
        public DateTimeOffset? LastRunUtc => null;
        public Task<int> ReconcileAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, bool force, CancellationToken ct) => Task.FromResult(0);
        public Task<int> ReconcileLookbackAsync(CancellationToken ct) { Interlocked.Increment(ref _lookbacks); return Task.FromResult(0); }
        public Task<int> RecheckSuspendedAsync(CancellationToken ct) => Task.FromResult(0);
    }

    private sealed class Rig
    {
        public CountingService Service { get; } = new();
        public ManualTimeProvider Clock { get; } = new();
        public TestLifetime Lifetime { get; } = new();
        public ReadinessState Readiness { get; } = new();
        public SubscriptionWorker Worker { get; }

        public Rig(string notificationUrl = "https://tcri.example.test/graph/notify")
        {
            Worker = new SubscriptionWorker(Service, Options.Create(new GraphOptions { NotificationUrl = notificationUrl }),
                Options.Create(new SubscriptionOptions()), Readiness, Lifetime, Clock, NullLogger<SubscriptionWorker>.Instance);
        }
    }

    private static async Task<bool> WaitFor(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < until)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }
        return condition();
    }

    [Fact]
    public async Task RunOnce_CallsEnsureOnce()
    {
        var rig = new Rig();

        await rig.Worker.RunOnceAsync(default);

        Assert.Equal(1, rig.Service.Calls);
    }

    [Fact]
    public async Task RunOnce_EnsureReturnsFalse_IsNotAnError()
    {
        var rig = new Rig();
        rig.Service.Result = _ => false;

        await rig.Worker.RunOnceAsync(default);

        Assert.Equal(1, rig.Service.Calls);
    }

    [Fact]
    public async Task RunOnce_EnsureThrows_IsSwallowed()
    {
        var rig = new Rig();
        rig.Service.Throws = _ => new InvalidOperationException("database unavailable");

        await rig.Worker.RunOnceAsync(default);

        Assert.Equal(1, rig.Service.Calls);
    }

    [Fact]
    public async Task Worker_WithoutNotificationUrl_DoesNothing()
    {
        var rig = new Rig(notificationUrl: "");
        rig.Readiness.SetReady();
        rig.Lifetime.Started();

        await rig.Worker.StartAsync(default);
        await rig.Worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, rig.Service.Calls);
        await rig.Worker.StopAsync(default);
    }

    [Fact]
    public async Task Worker_WaitsForReadinessAndForTheServer_ThenChecksOnce()
    {
        var rig = new Rig();
        await rig.Worker.StartAsync(default);

        await Task.Delay(150);
        Assert.Equal(0, rig.Service.Calls);

        rig.Readiness.SetReady();
        await Task.Delay(150);
        Assert.Equal(0, rig.Service.Calls);

        rig.Lifetime.Started();
        Assert.True(await WaitFor(() => rig.Service.Calls == 1));
        await rig.Worker.StopAsync(default);
    }

    [Fact]
    public async Task Worker_ChecksEveryRenewCheckMinutes()
    {
        var rig = new Rig();
        rig.Readiness.SetReady();
        rig.Lifetime.Started();
        await rig.Worker.StartAsync(default);
        Assert.True(await WaitFor(() => rig.Service.Calls == 1));
        await Task.Delay(100);   // let the worker start waiting for the next period

        rig.Clock.Advance(TimeSpan.FromMinutes(4));
        await Task.Delay(100);
        Assert.Equal(1, rig.Service.Calls);

        rig.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(await WaitFor(() => rig.Service.Calls == 2));
        await rig.Worker.StopAsync(default);
    }

    [Fact]
    public async Task Worker_AfterAFailedCheck_TriesAgainNextPeriod()
    {
        var rig = new Rig();
        rig.Service.Throws = n => n == 1 ? new InvalidOperationException("database unavailable") : null;
        rig.Readiness.SetReady();
        rig.Lifetime.Started();
        await rig.Worker.StartAsync(default);
        Assert.True(await WaitFor(() => rig.Service.Calls == 1));
        await Task.Delay(100);

        rig.Clock.Advance(TimeSpan.FromMinutes(5));

        Assert.True(await WaitFor(() => rig.Service.Calls == 2));
        await rig.Worker.StopAsync(default);
    }

    [Fact]
    public async Task Worker_StopsPromptly()
    {
        var rig = new Rig();
        rig.Readiness.SetReady();
        rig.Lifetime.Started();
        await rig.Worker.StartAsync(default);
        Assert.True(await WaitFor(() => rig.Service.Calls == 1));

        await rig.Worker.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(rig.Worker.ExecuteTask!.IsCompleted);
    }

    [Fact]
    public async Task SubscriptionFailure_DoesNotStopReconciliation()
    {
        var rig = new Rig();
        rig.Service.Throws = _ => new InvalidOperationException("database unavailable");
        rig.Readiness.SetReady();
        rig.Lifetime.Started();
        await rig.Worker.StartAsync(default);
        Assert.True(await WaitFor(() => rig.Service.Calls == 1));

        var reconciler = new CountingReconciler();
        var reconcile = new ReconcileWorker(reconciler, Options.Create(new ReconcileOptions()), rig.Readiness, rig.Clock,
            NullLogger<ReconcileWorker>.Instance);
        await reconcile.RunOnceAsync(default);

        Assert.Equal(1, reconciler.Lookbacks);
        Assert.False(rig.Worker.ExecuteTask!.IsFaulted);
        await rig.Worker.StopAsync(default);
    }
}
