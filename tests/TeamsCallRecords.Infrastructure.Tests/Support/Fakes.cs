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

    public Task<IReadOnlyList<GraphSubscriptionInfo>> ListSubscriptionsAsync(CancellationToken ct) => throw new NotSupportedException();
    public Task<GraphSubscription> CreateSubscriptionAsync(GraphSubscriptionRequest request, CancellationToken ct) => throw new NotSupportedException();
    public Task<GraphSubscription> RenewSubscriptionAsync(string subscriptionId, DateTimeOffset newExpiresUtc, CancellationToken ct) => throw new NotSupportedException();
    public Task DeleteSubscriptionAsync(string subscriptionId, CancellationToken ct) => throw new NotSupportedException();
    public Task SendActivityNotificationAsync(ActivityNotification notification, CancellationToken ct) => throw new NotSupportedException();
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
