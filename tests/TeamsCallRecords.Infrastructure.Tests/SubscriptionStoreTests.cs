using TeamsCallRecords.Infrastructure.Data;
using TeamsCallRecords.Infrastructure.Data.Entities;
using TeamsCallRecords.Infrastructure.Subscriptions;
using TeamsCallRecords.Infrastructure.Tests.Support;

namespace TeamsCallRecords.Infrastructure.Tests;

public class SubscriptionStoreTests
{
    private static readonly DateTimeOffset T0 = TestDb.T0;

    private static SubscriptionRow Row(string id, DateTimeOffset? created = null, string status = SubscriptionStatuses.Active,
        string clientState = "state-1", string url = "https://example.test/graph/notify") => new()
    {
        Id = id,
        Kind = SubscriptionKinds.CallRecords,
        Resource = "communications/callRecords",
        NotificationUrl = url,
        ExpiresUtc = UtcText.Format(T0.AddMinutes(4230)),
        ClientStateHash = SubscriptionStore.HashClientState(clientState),
        Status = status,
        CreatedUtc = UtcText.Format(created ?? T0),
        RenewedUtc = null,
    };

    private static SubscriptionStore NewStore(TestDb db) => new(db.Gate, db.Factory);

    [Fact]
    public async Task Upsert_NewRow_IsStored()
    {
        using var db = TestDb.Create();
        var store = NewStore(db);

        await store.UpsertAsync(Row("sub-1"), default);

        var rows = await store.ListAsync(default);
        var row = Assert.Single(rows);
        Assert.Equal("sub-1", row.Id);
        Assert.Equal(SubscriptionKinds.CallRecords, row.Kind);
        Assert.Equal("communications/callRecords", row.Resource);
        Assert.Equal("https://example.test/graph/notify", row.NotificationUrl);
        Assert.Equal(UtcText.Format(T0.AddMinutes(4230)), row.ExpiresUtc);
        Assert.Equal(SubscriptionStatuses.Active, row.Status);
        Assert.Equal(UtcText.Format(T0), row.CreatedUtc);
        Assert.Null(row.RenewedUtc);
    }

    [Fact]
    public async Task Upsert_ExistingId_ReplacesAllColumns()
    {
        using var db = TestDb.Create();
        var store = NewStore(db);
        await store.UpsertAsync(Row("sub-1"), default);

        var changed = Row("sub-1", created: T0.AddHours(1), status: SubscriptionStatuses.Renewing, clientState: "state-2", url: "https://other.test/graph/notify");
        changed.ExpiresUtc = UtcText.Format(T0.AddDays(5));
        changed.RenewedUtc = UtcText.Format(T0.AddHours(2));
        await store.UpsertAsync(changed, default);

        var row = Assert.Single(await store.ListAsync(default));
        Assert.Equal("https://other.test/graph/notify", row.NotificationUrl);
        Assert.Equal(UtcText.Format(T0.AddDays(5)), row.ExpiresUtc);
        Assert.Equal(SubscriptionStore.HashClientState("state-2"), row.ClientStateHash);
        Assert.Equal(SubscriptionStatuses.Renewing, row.Status);
        Assert.Equal(UtcText.Format(T0.AddHours(1)), row.CreatedUtc);
        Assert.Equal(UtcText.Format(T0.AddHours(2)), row.RenewedUtc);
    }

    [Fact]
    public async Task Upsert_SameRowObjectTwice_DoesNotFail()
    {
        using var db = TestDb.Create();
        var store = NewStore(db);
        var row = Row("sub-1");

        await store.UpsertAsync(row, default);
        row.Status = SubscriptionStatuses.Failed;
        await store.UpsertAsync(row, default);

        var stored = Assert.Single(await store.ListAsync(default));
        Assert.Equal(SubscriptionStatuses.Failed, stored.Status);
    }

    [Fact]
    public async Task List_Empty_ReturnsEmpty()
    {
        using var db = TestDb.Create();
        Assert.Empty(await NewStore(db).ListAsync(default));
    }

    [Fact]
    public async Task List_SortedByCreatedThenId()
    {
        using var db = TestDb.Create();
        var store = NewStore(db);
        await store.UpsertAsync(Row("sub-c", created: T0.AddHours(2)), default);
        await store.UpsertAsync(Row("sub-b", created: T0), default);
        await store.UpsertAsync(Row("sub-a", created: T0), default);

        var ids = (await store.ListAsync(default)).Select(r => r.Id).ToArray();

        Assert.Equal(new[] { "sub-a", "sub-b", "sub-c" }, ids);
    }

    [Fact]
    public async Task Get_ReturnsRowOrNull()
    {
        using var db = TestDb.Create();
        var store = NewStore(db);
        await store.UpsertAsync(Row("sub-1"), default);

        Assert.Equal("sub-1", (await store.GetAsync("sub-1", default))!.Id);
        Assert.Null(await store.GetAsync("missing", default));
    }

    [Fact]
    public async Task SetStatus_ChangesOnlyStatus()
    {
        using var db = TestDb.Create();
        var store = NewStore(db);
        await store.UpsertAsync(Row("sub-1"), default);

        await store.SetStatusAsync("sub-1", SubscriptionStatuses.Failed, default);

        var row = (await store.GetAsync("sub-1", default))!;
        Assert.Equal(SubscriptionStatuses.Failed, row.Status);
        Assert.Equal("https://example.test/graph/notify", row.NotificationUrl);
        Assert.Equal(UtcText.Format(T0.AddMinutes(4230)), row.ExpiresUtc);
    }

    [Fact]
    public async Task SetStatus_AbsentRow_DoesNothing()
    {
        using var db = TestDb.Create();
        var store = NewStore(db);

        await store.SetStatusAsync("missing", SubscriptionStatuses.Failed, default);

        Assert.Empty(await store.ListAsync(default));
    }

    [Fact]
    public async Task Delete_RemovesRow_AndIgnoresAbsent()
    {
        using var db = TestDb.Create();
        var store = NewStore(db);
        await store.UpsertAsync(Row("sub-1"), default);
        await store.UpsertAsync(Row("sub-2"), default);

        await store.DeleteAsync("sub-1", default);
        await store.DeleteAsync("missing", default);

        var row = Assert.Single(await store.ListAsync(default));
        Assert.Equal("sub-2", row.Id);
    }

    [Fact]
    public async Task Store_KeepsOnlyTheHash_NotTheClientState()
    {
        using var db = TestDb.Create();
        var store = NewStore(db);
        await store.UpsertAsync(Row("sub-1", clientState: "very-secret-state"), default);

        var rows = await db.QueryAsync("SELECT client_state_hash FROM subscription");

        var hash = Assert.IsType<string>(Assert.Single(rows)[0]);
        Assert.Equal(SubscriptionStore.HashClientState("very-secret-state"), hash);
        Assert.DoesNotContain("very-secret-state", hash);
    }

    [Fact]
    public void HashClientState_KnownVector()
    {
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", SubscriptionStore.HashClientState("abc"));
    }

    [Fact]
    public void HashClientState_Is64LowerHexChars()
    {
        var hash = SubscriptionStore.HashClientState("some-client-state");

        Assert.Equal(64, hash.Length);
        Assert.Equal(hash.ToLowerInvariant(), hash);
        Assert.All(hash, c => Assert.True(Uri.IsHexDigit(c)));
    }

    [Fact]
    public void Matches_SameState_True()
    {
        var hash = SubscriptionStore.HashClientState("state-1");

        Assert.True(SubscriptionStore.Matches(hash, "state-1"));
    }

    [Fact]
    public void Matches_OtherState_False()
    {
        var hash = SubscriptionStore.HashClientState("state-1");

        Assert.False(SubscriptionStore.Matches(hash, "state-2"));
        Assert.False(SubscriptionStore.Matches(hash, ""));
    }

    [Fact]
    public void Matches_StoredHashOfWrongLengthOrEmpty_False()
    {
        var hash = SubscriptionStore.HashClientState("state-1");

        Assert.False(SubscriptionStore.Matches(hash[..63], "state-1"));
        Assert.False(SubscriptionStore.Matches(hash + "0", "state-1"));
        Assert.False(SubscriptionStore.Matches("", "state-1"));
    }
}
