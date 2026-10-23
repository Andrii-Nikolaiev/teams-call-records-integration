namespace TeamsCallRecords.Infrastructure.Diagnostics;

/// <summary>Database error log (TRP 3.17). Never pass secrets or source-record bodies.</summary>
public interface IErrorLog
{
    Task WriteAsync(string component, string message, string? details, CancellationToken ct);
}
