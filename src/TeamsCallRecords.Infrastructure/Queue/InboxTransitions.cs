using TeamsCallRecords.Infrastructure.Data;
using TeamsCallRecords.Infrastructure.Data.Entities;

namespace TeamsCallRecords.Infrastructure.Queue;

/// <summary>
/// Inbox item transitions inside an existing write transaction (TRP 3.12.2). The caller invokes SaveChanges.
/// Deviation from the TRP signature: Reschedule also takes nowText, needed for done_utc when the item is
/// merged into an existing "new" item with the same element_hash (TRP 3.12.3, uq_inbox_new_hash).
/// </summary>
internal static class InboxTransitions
{
    public static void MarkDone(TcriDbContext db, long id, string nowText)
    {
        var row = Find(db, id);
        row.State = InboxStates.Done;
        row.DoneUtc = nowText;
        row.LockedUntilUtc = null;
        row.LastError = null;
    }

    public static void Reschedule(TcriDbContext db, long id, string nowText, string nextText, string? error, bool resetAttempts)
    {
        var row = Find(db, id);

        // A repeat notification B (same hash) may already be "new": merge instead of violating uq_inbox_new_hash.
        var twin = db.InboxItems.FirstOrDefault(x =>
            x.ElementHash == row.ElementHash && x.State == InboxStates.New && x.Id != id);
        if (twin != null)
        {
            if (string.CompareOrdinal(nextText, twin.NextAttemptUtc) < 0) twin.NextAttemptUtc = nextText;
            row.State = InboxStates.Done;
            row.DoneUtc = nowText;
            row.LockedUntilUtc = null;
            row.LastError = "superseded by a repeated notification";
            return;
        }

        row.State = InboxStates.New;
        row.NextAttemptUtc = nextText;
        row.LockedUntilUtc = null;
        row.LastError = error;
        if (resetAttempts) row.Attempts = 0;
    }

    public static void MarkFailed(TcriDbContext db, long id, string nowText, string error)
    {
        var row = Find(db, id);
        row.State = InboxStates.Failed;
        row.DoneUtc = nowText;
        row.LockedUntilUtc = null;
        row.LastError = error;
    }

    private static InboxRow Find(TcriDbContext db, long id) =>
        db.InboxItems.Find(id) ?? throw new InvalidOperationException($"inbox item {id} not found");
}
