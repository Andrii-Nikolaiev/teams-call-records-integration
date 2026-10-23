namespace TeamsCallRecords.Infrastructure.Data.Entities;

// Values match the CHECK constraints in InitialCreate.sql (TRP 3.11.1).
// String literals for these values are not used elsewhere.

public static class InboxKinds
{
    public const string CallRecord = "callRecord";
    public const string ChannelMessage = "channelMessage";
    public const string Reconcile = "reconcile";
}

public static class InboxStates
{
    public const string New = "new";
    public const string Processing = "processing";
    public const string Done = "done";
    public const string Failed = "failed";
}

public static class SourceStates
{
    public const string Pending = "pending";
    public const string Done = "done";
    public const string Suspended = "suspended";
    public const string Expired = "expired";
}

public static class OutboxStates
{
    public const string New = "new";
    public const string Sending = "sending";
    public const string Sent = "sent";
    public const string Failed = "failed";
}

public static class OutboxKinds
{
    public const string Activity = "activity";
}

public static class SubscriptionKinds
{
    public const string CallRecords = "callRecords";
    public const string ChannelMessages = "channelMessages";
}

public static class SubscriptionStatuses
{
    public const string Active = "active";
    public const string Renewing = "renewing";
    public const string Failed = "failed";
}

public static class AuditKinds
{
    public const string VersionDiscrepancy = "version_discrepancy";
}

public static class Components
{
    public const string Inbox = "inbox";
    public const string Processor = "processor";
    public const string Reconciler = "reconciler";
    public const string Outbox = "outbox";
    public const string Subscriptions = "subscriptions";
    public const string Graph = "graph";
    public const string Startup = "startup";
}
