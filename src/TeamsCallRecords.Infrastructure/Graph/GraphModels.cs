namespace TeamsCallRecords.Infrastructure.Graph;

public enum GraphFetchStatus { Ok, NotFound, PagesIncomplete, Malformed }

public sealed record GraphFetchResult(GraphFetchStatus Status, string? Json);
public sealed record GraphCallRecordRef(string Id, long? Version, DateTimeOffset? StartUtc);
public sealed record GraphSubscriptionRequest(string Kind, string Resource, string NotificationUrl, DateTimeOffset ExpiresUtc, string ClientState);
public sealed record GraphSubscription(string Id, string Resource, DateTimeOffset ExpiresUtc);
public sealed record GraphSubscriptionInfo(string Id, string Resource, string NotificationUrl, DateTimeOffset ExpiresUtc);
public sealed record ActivityNotification(string RecipientUserId, string ActivityType, string TopicText, string PreviewText,
    IReadOnlyDictionary<string, string> TemplateParameters);
