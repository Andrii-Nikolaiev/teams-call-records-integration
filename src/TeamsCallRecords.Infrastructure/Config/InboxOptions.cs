namespace TeamsCallRecords.Infrastructure.Config;

/// <summary>Section "Inbox" (TRP 3.18).</summary>
public sealed class InboxOptions
{
    public int MaxPending { get; set; } = 10000;

    public IEnumerable<string> Validate()
    {
        if (MaxPending < 1 || MaxPending > 1_000_000) yield return "Inbox:MaxPending must be 1..1000000";
    }
}
