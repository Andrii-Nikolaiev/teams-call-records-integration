using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace TeamsCallRecords.Infrastructure.Data;

/// <summary>
/// Executes the PRAGMA set on every opened SQLite connection (TRP 3.11.3). synchronous=FULL is required
/// because Graph is answered with 202 only after the inbox insert is committed.
/// </summary>
public sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    internal const string Pragmas =
        "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = connection.CreateCommand();
        command.CommandText = Pragmas;
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = Pragmas;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
