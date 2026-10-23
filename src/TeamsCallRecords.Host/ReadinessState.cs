namespace TeamsCallRecords.Host;

/// <summary>Set once the database is migrated; workers and /health/ready wait for it (TRP 3.25, StartupBarrier).</summary>
public sealed class ReadinessState
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsReady => _ready.Task.IsCompleted;
    public Task WhenReady => _ready.Task;
    public void SetReady() => _ready.TrySetResult();
}
