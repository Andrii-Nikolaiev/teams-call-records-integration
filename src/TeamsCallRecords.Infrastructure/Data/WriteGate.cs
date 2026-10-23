using Microsoft.EntityFrameworkCore;

namespace TeamsCallRecords.Infrastructure.Data;

/// <summary>
/// One writing transaction at a time. The caller calls SaveChangesAsync itself; the gate commits.
/// A regular transaction is enough: the process is the only writer.
/// </summary>
public sealed class WriteGate : IWriteGate
{
    private readonly IDbContextFactory<TcriDbContext> _factory;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public WriteGate(IDbContextFactory<TcriDbContext> factory) => _factory = factory;

    public async Task<T> RunAsync<T>(Func<TcriDbContext, CancellationToken, Task<T>> work, CancellationToken ct)
    {
        await _semaphore.WaitAsync(ct);
        try
        {
            await using var db = await _factory.CreateDbContextAsync(ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            try
            {
                var result = await work(db, ct);
                await tx.CommitAsync(ct);
                return result;
            }
            catch
            {
                await tx.RollbackAsync(CancellationToken.None);
                throw;
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public Task RunAsync(Func<TcriDbContext, CancellationToken, Task> work, CancellationToken ct) =>
        RunAsync<int>(async (db, token) =>
        {
            await work(db, token);
            return 0;
        }, ct);
}
