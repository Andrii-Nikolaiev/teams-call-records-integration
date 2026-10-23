namespace TeamsCallRecords.Infrastructure.Graph;

/// <summary>Microsoft Graph failure (TRP 3.14.1). Messages never include response bodies or tokens.</summary>
public sealed class GraphException : Exception
{
    public GraphException(string message, int? statusCode, bool isTransient, Exception? inner = null, bool outcomeUnknown = false)
        : base(message, inner)
    {
        StatusCode = statusCode;
        IsTransient = isTransient;
        OutcomeUnknown = outcomeUnknown;
    }

    public int? StatusCode { get; }
    public bool IsTransient { get; }
    public bool OutcomeUnknown { get; }
}
