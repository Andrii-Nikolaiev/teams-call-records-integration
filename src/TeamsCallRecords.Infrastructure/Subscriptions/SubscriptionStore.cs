using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using TeamsCallRecords.Infrastructure.Data;
using TeamsCallRecords.Infrastructure.Data.Entities;

namespace TeamsCallRecords.Infrastructure.Subscriptions;

/// <summary>Subscription table on top of the write gate (TRP 3.15.2). Writes go through <see cref="IWriteGate"/>, reads use a fresh context.</summary>
public sealed class SubscriptionStore : ISubscriptionStore
{
    private readonly IWriteGate _gate;
    private readonly IDbContextFactory<TcriDbContext> _factory;

    public SubscriptionStore(IWriteGate gate, IDbContextFactory<TcriDbContext> factory)
    {
        _gate = gate;
        _factory = factory;
    }

    /// <summary>SHA-256 of the UTF-8 text, lowercase hex, 64 characters.</summary>
    public static string HashClientState(string clientState)
    {
        ArgumentNullException.ThrowIfNull(clientState);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(clientState))).ToLowerInvariant();
    }

    /// <summary>Constant-time comparison of the stored hash with the hash of the supplied clientState; false on different lengths.</summary>
    public static bool Matches(string storedHash, string clientState)
    {
        if (string.IsNullOrEmpty(storedHash) || clientState is null) return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(storedHash), Encoding.UTF8.GetBytes(HashClientState(clientState)));
    }

    public async Task<IReadOnlyList<SubscriptionRow>> ListAsync(CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Subscriptions.AsNoTracking().OrderBy(x => x.CreatedUtc).ThenBy(x => x.Id).ToListAsync(ct);
    }

    public async Task<SubscriptionRow?> GetAsync(string id, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Subscriptions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
    }

    public Task UpsertAsync(SubscriptionRow row, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(row);
        return _gate.RunAsync(async (db, token) =>
        {
            var existing = await db.Subscriptions.FirstOrDefaultAsync(x => x.Id == row.Id, token);
            if (existing is null) db.Subscriptions.Add(Copy(row));
            else db.Entry(existing).CurrentValues.SetValues(row);
            await db.SaveChangesAsync(token);
        }, ct);
    }

    public Task SetStatusAsync(string id, string status, CancellationToken ct) =>
        _gate.RunAsync(async (db, token) =>
        {
            var existing = await db.Subscriptions.FirstOrDefaultAsync(x => x.Id == id, token);
            if (existing is null) return;
            existing.Status = status;
            await db.SaveChangesAsync(token);
        }, ct);

    public Task DeleteAsync(string id, CancellationToken ct) =>
        _gate.RunAsync(async (db, token) =>
        {
            var existing = await db.Subscriptions.FirstOrDefaultAsync(x => x.Id == id, token);
            if (existing is null) return;
            db.Subscriptions.Remove(existing);
            await db.SaveChangesAsync(token);
        }, ct);

    private static SubscriptionRow Copy(SubscriptionRow r) => new()
    {
        Id = r.Id,
        Kind = r.Kind,
        Resource = r.Resource,
        NotificationUrl = r.NotificationUrl,
        ExpiresUtc = r.ExpiresUtc,
        ClientStateHash = r.ClientStateHash,
        Status = r.Status,
        CreatedUtc = r.CreatedUtc,
        RenewedUtc = r.RenewedUtc,
    };
}
