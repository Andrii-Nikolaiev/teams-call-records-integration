namespace TeamsCallRecords.Infrastructure.Data.Entities;

/// <summary>Table error_log.</summary>
public sealed class ErrorLogRow
{
    public long Id { get; set; }
    public string Ts { get; set; } = "";
    public string Component { get; set; } = "";
    public string Message { get; set; } = "";
    public string? Details { get; set; }
}
