using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using TeamsCallRecords.Infrastructure.Data;
using TeamsCallRecords.Infrastructure.Data.Entities;
using TeamsCallRecords.Infrastructure.Tests.Support;

namespace TeamsCallRecords.Infrastructure.Tests;

public class SchemaTests
{
    private static readonly string[] Tables =
        { "audit_log", "cdr", "error_log", "inbox", "outbox", "rejected", "source_state", "subscription" };

    [Fact]
    public async Task Migration_CreatesEightTables()
    {
        using var db = TestDb.Create();
        var rows = await db.QueryAsync(
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name");
        var names = rows.Select(r => (string)r[0]!).ToList();

        Assert.Equal(Tables.Append("__EFMigrationsHistory").OrderBy(n => n, StringComparer.Ordinal), names);
    }

    [Fact]
    public async Task Migration_CreatesNamedIndexesAndKeys()
    {
        using var db = TestDb.Create();
        var indexes = (await db.QueryAsync("SELECT name FROM sqlite_master WHERE type = 'index'"))
            .Select(r => (string)r[0]!).ToHashSet();

        foreach (var name in new[]
                 {
                     "ix_cdr_start", "ix_cdr_call", "ix_source_state_due", "uq_inbox_new_hash", "ix_inbox_hash",
                     "ix_inbox_call", "ix_inbox_due", "ix_rejected_created", "ix_outbox_due", "ix_error_log_ts",
                 })
            Assert.Contains(name, indexes);
        Assert.DoesNotContain("uq_inbox_hash", indexes);

        // UNIQUE constraints are named in the table DDL (SQLite names their indexes sqlite_autoindex_*).
        var cdrSql = (string)(await db.QueryAsync("SELECT sql FROM sqlite_master WHERE name = 'cdr'"))[0][0]!;
        var outboxSql = (string)(await db.QueryAsync("SELECT sql FROM sqlite_master WHERE name = 'outbox'"))[0][0]!;
        Assert.Contains("CONSTRAINT uq_cdr_key UNIQUE", cdrSql);
        Assert.Contains("CONSTRAINT uq_outbox_key UNIQUE", outboxSql);
    }

    [Fact]
    public async Task Migration_IsIdempotent()
    {
        using var db = TestDb.Create();
        await db.ReadAsync(async ctx =>
        {
            await ctx.Database.MigrateAsync();
            return 0;
        });

        var history = await db.QueryAsync("SELECT MigrationId FROM __EFMigrationsHistory");
        Assert.Single(history);
        Assert.Equal("20261021000000_InitialCreate", history[0][0]);
        var tables = await db.QueryAsync(
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'");
        Assert.Equal(9L, tables[0][0]);
    }

    [Fact]
    public async Task Connection_UsesWalFullForeignKeysBusyTimeout()
    {
        using var db = TestDb.Create();
        Assert.Equal("wal", (await db.QueryAsync("PRAGMA journal_mode"))[0][0]);
        Assert.Equal(2L, (await db.QueryAsync("PRAGMA synchronous"))[0][0]);
        Assert.Equal(1L, (await db.QueryAsync("PRAGMA foreign_keys"))[0][0]);
        Assert.Equal(5000L, (await db.QueryAsync("PRAGMA busy_timeout"))[0][0]);
    }

    [Fact]
    public async Task CheckConstraint_RejectsUnknownState()
    {
        using var db = TestDb.Create();
        await Assert.ThrowsAsync<SqliteException>(() => db.ReadAsync(ctx => ctx.Database.ExecuteSqlRawAsync(InboxInsert("h1", "bogus"))));

        var count = (await db.QueryAsync("SELECT COUNT(*) FROM inbox"))[0][0];
        Assert.Equal(0L, count);
    }

    [Fact]
    public async Task Inbox_UniqueIndexIsPartial()
    {
        using var db = TestDb.Create();

        // (new, new): the second insert violates uq_inbox_new_hash.
        await db.ReadAsync(ctx => ctx.Database.ExecuteSqlRawAsync(InboxInsert("p1", "new")));
        await Assert.ThrowsAsync<SqliteException>(() => db.ReadAsync(ctx => ctx.Database.ExecuteSqlRawAsync(InboxInsert("p1", "new"))));

        // (done, new): allowed, only rows in state 'new' count as duplicates.
        await db.ReadAsync(ctx => ctx.Database.ExecuteSqlRawAsync(InboxInsert("p2", "done")));
        await db.ReadAsync(ctx => ctx.Database.ExecuteSqlRawAsync(InboxInsert("p2", "new")));

        // (done, done): allowed.
        await db.ReadAsync(ctx => ctx.Database.ExecuteSqlRawAsync(InboxInsert("p3", "done")));
        await db.ReadAsync(ctx => ctx.Database.ExecuteSqlRawAsync(InboxInsert("p3", "done")));

        var count = (await db.QueryAsync("SELECT COUNT(*) FROM inbox"))[0][0];
        Assert.Equal(5L, count);
    }

    [Fact]
    public async Task Model_ColumnsMatchMigratedTables()
    {
        using var db = TestDb.Create();
        await db.ReadAsync(async ctx =>
        {
            foreach (var entity in ctx.Model.GetEntityTypes())
            {
                var table = entity.GetTableName()!;
                var id = StoreObjectIdentifier.Table(table, null);
                var info = await TableInfoAsync(db, table);

                var expected = entity.GetProperties()
                    .ToDictionary(p => p.GetColumnName(id)!, p => p, StringComparer.Ordinal);

                Assert.Equal(expected.Keys.OrderBy(k => k, StringComparer.Ordinal),
                             info.Keys.OrderBy(k => k, StringComparer.Ordinal));

                foreach (var (column, property) in expected)
                {
                    Assert.Equal(Snake(property.Name), column);
                    // Primary-key columns are exempt: SQLite reports notnull=0 for rowid keys and for a
                    // single-column TEXT key declared without NOT NULL.
                    if (info[column].IsPrimaryKey) continue;
                    Assert.Equal(!property.IsNullable, info[column].NotNull);
                }
            }
            return 0;
        });
    }

    [Fact]
    public async Task Data_SurvivesRestart()
    {
        using var first = TestDb.Create();
        await first.Gate.RunAsync(async (ctx, ct) =>
        {
            ctx.ErrorLog.Add(new ErrorLogRow
            {
                Ts = UtcText.Now(first.Clock),
                Component = Components.Startup,
                Message = "persisted",
                Details = "d",
            });
            await ctx.SaveChangesAsync(ct);
        }, CancellationToken.None);

        using var second = first.Reopen();
        var row = await second.ReadAsync(ctx => ctx.ErrorLog.AsNoTracking().SingleAsync());
        Assert.Equal("persisted", row.Message);
        Assert.Equal("d", row.Details);
        Assert.Equal("2026-10-20T18:00:00.0000000Z", row.Ts);
    }

    private static string InboxInsert(string hash, string state) =>
        "INSERT INTO inbox (element_hash, kind, tenant_id, call_id, raw, state, attempts, next_attempt_utc, received_utc) " +
        $"VALUES ('{hash}', 'callRecord', 't', 'c', 'raw', '{state}', 0, '2026-10-20T18:00:00.0000000Z', '2026-10-20T18:00:00.0000000Z')";

    private static string Snake(string name) => Regex.Replace(name, "(?<!^)([A-Z])", "_$1").ToLowerInvariant();

    private sealed record Column(bool NotNull, bool IsPrimaryKey);

    private static async Task<Dictionary<string, Column>> TableInfoAsync(TestDb db, string table)
    {
        var rows = await db.QueryAsync($"PRAGMA table_info('{table}')");
        // columns: cid, name, type, notnull, dflt_value, pk
        return rows.ToDictionary(r => (string)r[1]!, r => new Column((long)r[3]! != 0, (long)r[5]! != 0), StringComparer.Ordinal);
    }
}
