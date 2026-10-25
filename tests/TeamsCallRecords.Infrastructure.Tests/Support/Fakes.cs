using TeamsCallRecords.Infrastructure.Graph;
using TeamsCallRecords.Infrastructure.Processing;

namespace TeamsCallRecords.Infrastructure.Tests.Support;

public sealed class FakeGraphClient : IGraphClient
{
    public Func<string, CancellationToken, Task<GraphFetchResult>> OnGetCallRecord { get; set; } =
        (_, _) => Task.FromResult(new GraphFetchResult(GraphFetchStatus.NotFound, null));
    public List<string> Requested { get; } = new();

    public void Returns(GraphFetchStatus status, string? json = null) =>
        OnGetCallRecord = (_, _) => Task.FromResult(new GraphFetchResult(status, json));

    public void Throws(Exception e) => OnGetCallRecord = (_, _) => throw e;

    public Task<GraphFetchResult> GetCallRecordAsync(string callId, CancellationToken ct)
    {
        Requested.Add(callId);
        return OnGetCallRecord(callId, ct);
    }

    public Func<DateTimeOffset, DateTimeOffset, IReadOnlyList<GraphCallRecordRef>> OnList { get; set; } = (_, _) => Array.Empty<GraphCallRecordRef>();
    public Task<IReadOnlyList<GraphCallRecordRef>> ListCallRecordsAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct) =>
        Task.FromResult(OnList(fromUtc, toUtc));

    // ---- subscriptions: a small in-memory Graph; faults can be injected ----------------------------------

    /// <summary>What Graph currently holds. List/Create/Renew/Delete work against this list.</summary>
    public List<GraphSubscriptionInfo> Subscriptions { get; } = new();
    /// <summary>Calls in order: "list", "create:&lt;resource&gt;", "renew:&lt;id&gt;", "delete:&lt;id&gt;".</summary>
    public List<string> SubscriptionCalls { get; } = new();
    public List<GraphSubscriptionRequest> CreateRequests { get; } = new();
    public Exception? CreateFault { get; set; }
    /// <summary>When true, a faulty Create is applied in Graph before the exception (the response was lost).</summary>
    public bool CreateFaultAfterApply { get; set; }
    public Exception? RenewFault { get; set; }
    public Exception? DeleteFault { get; set; }
    private int _subscriptionCounter;

    public Task<IReadOnlyList<GraphSubscriptionInfo>> ListSubscriptionsAsync(CancellationToken ct)
    {
        SubscriptionCalls.Add("list");
        return Task.FromResult<IReadOnlyList<GraphSubscriptionInfo>>(Subscriptions.ToList());
    }

    public Task<GraphSubscription> CreateSubscriptionAsync(GraphSubscriptionRequest request, CancellationToken ct)
    {
        SubscriptionCalls.Add("create:" + request.Resource);
        CreateRequests.Add(request);
        if (CreateFault != null && !CreateFaultAfterApply) throw CreateFault;
        var id = "graph-sub-" + ++_subscriptionCounter;
        Subscriptions.Add(new GraphSubscriptionInfo(id, request.Resource, request.NotificationUrl, request.ExpiresUtc));
        if (CreateFault != null) throw CreateFault;
        return Task.FromResult(new GraphSubscription(id, request.Resource, request.ExpiresUtc));
    }

    public Task<GraphSubscription> RenewSubscriptionAsync(string subscriptionId, DateTimeOffset newExpiresUtc, CancellationToken ct)
    {
        SubscriptionCalls.Add("renew:" + subscriptionId);
        if (RenewFault != null) throw RenewFault;
        var index = Subscriptions.FindIndex(s => s.Id == subscriptionId);
        if (index < 0) throw new GraphException("PATCH subscriptions: not found", 404, false);
        var old = Subscriptions[index];
        Subscriptions[index] = old with { ExpiresUtc = newExpiresUtc };
        return Task.FromResult(new GraphSubscription(old.Id, old.Resource, newExpiresUtc));
    }

    public Task DeleteSubscriptionAsync(string subscriptionId, CancellationToken ct)
    {
        SubscriptionCalls.Add("delete:" + subscriptionId);
        if (DeleteFault != null) throw DeleteFault;
        Subscriptions.RemoveAll(s => s.Id == subscriptionId);
        return Task.CompletedTask;
    }

    public List<ActivityNotification> Sent { get; } = new();
    public Func<ActivityNotification, CancellationToken, Task> OnSend { get; set; } = (_, _) => Task.CompletedTask;

    public async Task SendActivityNotificationAsync(ActivityNotification notification, CancellationToken ct)
    {
        Sent.Add(notification);
        await OnSend(notification, ct);
    }
}

public sealed class FakeReconciler : IReconciler
{
    public List<(DateTimeOffset From, DateTimeOffset To, bool Force)> RangeCalls { get; } = new();
    public int LookbackCalls { get; private set; }
    public DateTimeOffset? LastRunUtc => null;

    public Task<int> ReconcileAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, bool force, CancellationToken ct)
    {
        RangeCalls.Add((fromUtc, toUtc, force));
        return Task.FromResult(0);
    }

    public Task<int> ReconcileLookbackAsync(CancellationToken ct)
    {
        LookbackCalls++;
        return Task.FromResult(0);
    }

    public Task<int> RecheckSuspendedAsync(CancellationToken ct) => Task.FromResult(0);
}

public sealed class FakeCdrApplier : ICdrApplier
{
    public int Calls { get; private set; }
    public Func<ApplyRequest, ApplyResult> OnApply { get; set; } = _ => new ApplyResult(ApplyStatus.Applied, null, 0, 0, 0, 0);

    public Task<ApplyResult> ApplyAsync(ApplyRequest request, CancellationToken ct)
    {
        Calls++;
        return Task.FromResult(OnApply(request));
    }
}
