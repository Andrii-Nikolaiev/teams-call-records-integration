namespace TeamsCallRecords.Infrastructure.Config;

/// <summary>Section "Inbox" (TRP 3.18).</summary>
public sealed class InboxOptions
{
    public int MaxPending { get; set; } = 10000;
}
