using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using TeamsCallRecords.Infrastructure.Data;

namespace TeamsCallRecords.Infrastructure.Tests.Support;

/// <summary>
/// Temporary real SQLite database (not :memory:, because WAL, CHECK constraints and restart are tested),
/// a context factory with the PRAGMA interceptor, a write gate and a controllable clock.
/// </summary>
public sealed class TestDb : IDisposable
{
    public static readonly DateTimeOffset T0 = new(2026, 10, 20, 18, 0, 0, TimeSpan.Zero);
    public const string Tenant = "11111111-1111-4111-8111-111111111111";

    private readonly string _directory;
    private bool _ownsDirectory = true;

    public string DbPath { get; }
    public FakeTimeProvider Clock { get; }
    public IDbContextFactory<TcriDbContext> Factory { get; }
    public IWriteGate Gate { get; }

    private TestDb(string directory, DateTimeOffset start)
    {
        _directory = directory;
        DbPath = Path.Combine(directory, "tcri.db");
        Clock = new FakeTimeProvider(start);
        var options = new DbContextOptionsBuilder<TcriDbContext>()
            .UseSqlite("Data Source=" + DbPath)
            .AddInterceptors(new SqlitePragmaInterceptor())
            .Options;
        Factory = new SimpleFactory(options);
        Gate = new WriteGate(Factory);
        using var db = Factory.CreateDbContext();
        db.Database.Migrate();
    }

    public static TestDb Create(DateTimeOffset? start = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "tcri-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return new TestDb(dir, start ?? T0);
    }

    /// <summary>A new instance over the same file; the previous one is closed and no longer owns the directory.</summary>
    public TestDb Reopen()
    {
        _ownsDirectory = false;
        SqliteConnection.ClearAllPools();
        return new TestDb(_directory, Clock.GetUtcNow());
    }

    /// <summary>Reader without the write gate.</summary>
    public async Task<T> ReadAsync<T>(Func<TcriDbContext, Task<T>> read)
    {
        await using var db = await Factory.CreateDbContextAsync();
        return await read(db);
    }

    /// <summary>Runs raw SQL through a context connection (so the PRAGMA interceptor runs) and returns all rows.</summary>
    public Task<List<object?[]>> QueryAsync(string sql) => ReadAsync(async db =>
    {
        await db.Database.OpenConnectionAsync();
        var rows = new List<object?[]>();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++)
                row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    });

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (!_ownsDirectory) return;
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* a temporary directory may stay on disk */ }
    }

    private sealed class SimpleFactory : IDbContextFactory<TcriDbContext>
    {
        private readonly DbContextOptions<TcriDbContext> _options;
        public SimpleFactory(DbContextOptions<TcriDbContext> options) => _options = options;
        public TcriDbContext CreateDbContext() => new(_options);
    }
}
