using Microsoft.Extensions.Logging;
using TeamsCallRecords.Infrastructure.Data;
using TeamsCallRecords.Infrastructure.Data.Entities;

namespace TeamsCallRecords.Infrastructure.Diagnostics;

public sealed class ErrorLog : IErrorLog
{
    public const int MaxMessage = 1000;
    public const int MaxDetails = 4000;

    private readonly IWriteGate _gate;
    private readonly TimeProvider _clock;
    private readonly ILogger<ErrorLog> _log;

    public ErrorLog(IWriteGate gate, TimeProvider clock, ILogger<ErrorLog> log)
    {
        _gate = gate;
        _clock = clock;
        _log = log;
    }

    public static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    public async Task WriteAsync(string component, string message, string? details, CancellationToken ct)
    {
        try
        {
            await _gate.RunAsync(async (db, token) =>
            {
                db.ErrorLog.Add(new ErrorLogRow
                {
                    Ts = UtcText.Now(_clock),
                    Component = component,
                    Message = Truncate(message, MaxMessage),
                    Details = details == null ? null : Truncate(details, MaxDetails),
                });
                await db.SaveChangesAsync(token);
            }, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            // the error logger must not cause new errors
            _log.LogError(e, "Could not write error_log: {Component}: {Message}", component, message);
        }
    }
}
