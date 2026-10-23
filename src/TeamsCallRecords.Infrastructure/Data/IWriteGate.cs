namespace TeamsCallRecords.Infrastructure.Data;

/// <summary>Serializes all database writes of the process (TRP 3.11.4). The work must not call RunAsync again.</summary>
public interface IWriteGate
{
    Task<T> RunAsync<T>(Func<TcriDbContext, CancellationToken, Task<T>> work, CancellationToken ct);
    Task RunAsync(Func<TcriDbContext, CancellationToken, Task> work, CancellationToken ct);
}
