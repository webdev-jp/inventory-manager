using InventoryManager.Core.Offboarding;
using InventoryManager.Core.Persistence;

namespace InventoryManager.Core.Tests;

public class ScheduledTerminationTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 14, 15, 0, 0, DateTimeKind.Utc);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"schedterm_{Guid.NewGuid():N}");
    private readonly ScheduledTerminationStore _store;

    public ScheduledTerminationTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new ScheduledTerminationStore(Path.Combine(_dir, "scheduled-terminations.json"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private static ScheduledTermination Make(string username, DateTime due) => new()
    {
        Username = username,
        DisplayName = username.ToUpperInvariant(),
        DeviceName = $"PC-{username}",
        ItemId = 7,
        DueAtUtc = due,
        Ticket = "#IT1",
        ScheduledBy = "sualice",
        ScheduledAtUtc = Now.AddDays(-1),
    };

    // ---- rules ----

    [Fact]
    public void IsDue_OnlyWhenPendingAndTimePassed()
    {
        Assert.True(ScheduledTerminationRules.IsDue(Make("a", Now.AddMinutes(-1)), Now));
        Assert.True(ScheduledTerminationRules.IsDue(Make("a", Now), Now));
        Assert.False(ScheduledTerminationRules.IsDue(Make("a", Now.AddMinutes(1)), Now));

        var done = Make("a", Now.AddDays(-1));
        done.State = ScheduledTerminationState.Completed;
        Assert.False(ScheduledTerminationRules.IsDue(done, Now));
        done.State = ScheduledTerminationState.Cancelled;
        Assert.False(ScheduledTerminationRules.IsDue(done, Now));
    }

    [Fact]
    public void DueForPrompt_SkipsFutureResolvedAndOtherAdminsLiveClaims_EarliestFirst()
    {
        var late = Make("late", Now.AddHours(-1));
        var early = Make("early", Now.AddDays(-2));
        var future = Make("future", Now.AddHours(3));
        var claimed = Make("claimed", Now.AddHours(-3));
        claimed.ClaimedBy = "subob";
        claimed.ClaimedAtUtc = Now.AddMinutes(-10);
        var mine = Make("mine", Now.AddHours(-4));
        mine.ClaimedBy = "sualice";
        mine.ClaimedAtUtc = Now.AddMinutes(-10);

        var result = ScheduledTerminationRules.DueForPrompt([late, early, future, claimed, mine], "sualice", Now);

        Assert.Equal(["early", "mine", "late"], result.Select(t => t.Username));
    }

    [Fact]
    public void StaleClaimFromAnotherAdmin_NoLongerBlocksThePrompt()
    {
        var item = Make("stale", Now.AddHours(-5));
        item.ClaimedBy = "subob";
        item.ClaimedAtUtc = Now - ScheduledTerminationRules.ClaimExpiresAfter - TimeSpan.FromMinutes(1);

        Assert.False(ScheduledTerminationRules.IsClaimedByOther(item, "sualice", Now));
        Assert.Single(ScheduledTerminationRules.DueForPrompt([item], "sualice", Now));
    }

    [Fact]
    public void Describe_ReadsNaturally()
    {
        Assert.Equal("In 2 days", ScheduledTerminationRules.Describe(Make("a", Now.AddDays(2).AddHours(1)), "me", Now));
        Assert.Equal("In 3 hours", ScheduledTerminationRules.Describe(Make("a", Now.AddHours(3).AddMinutes(5)), "me", Now));
        Assert.Equal("DUE NOW", ScheduledTerminationRules.Describe(Make("a", Now.AddSeconds(-20)), "me", Now));
        Assert.StartsWith("DUE - overdue", ScheduledTerminationRules.Describe(Make("a", Now.AddHours(-2)), "me", Now));
        var claimed = Make("a", Now.AddHours(-2));
        claimed.ClaimedBy = "subob";
        claimed.ClaimedAtUtc = Now.AddMinutes(-5);
        Assert.Equal("Being handled by subob", ScheduledTerminationRules.Describe(claimed, "me", Now));
    }

    // ---- store ----

    [Fact]
    public void MissingFile_IsEmpty()
    {
        Assert.Empty(_store.Load());
        Assert.Empty(_store.LoadPending());
    }

    [Fact]
    public void Add_PersistsAndRoundTrips()
    {
        var added = _store.Add(Make("jsmith", Now.AddDays(3)));

        Assert.NotNull(added);
        var loaded = Assert.Single(new ScheduledTerminationStore(_store.FilePath).LoadPending());
        Assert.Equal("jsmith", loaded.Username);
        Assert.Equal("#IT1", loaded.Ticket);
        Assert.Equal(Now.AddDays(3), loaded.DueAtUtc);
        Assert.Equal(7, loaded.ItemId);
    }

    [Fact]
    public void Add_SecondPendingForSameUser_IsRejected_CaseInsensitive()
    {
        Assert.NotNull(_store.Add(Make("jsmith", Now.AddDays(1))));
        Assert.Null(_store.Add(Make("JSmith", Now.AddDays(2))));
        Assert.Single(_store.Load());
    }

    [Fact]
    public void Add_AfterPreviousWasCancelled_IsAllowed()
    {
        var first = _store.Add(Make("jsmith", Now.AddDays(1)))!;
        Assert.True(_store.Cancel(first.Id, "sualice", Now, "changed plans"));

        Assert.NotNull(_store.Add(Make("jsmith", Now.AddDays(5))));
        Assert.Single(_store.LoadPending());
    }

    [Fact]
    public void Reschedule_ChangesPendingOnly()
    {
        var item = _store.Add(Make("jsmith", Now.AddDays(1)))!;

        Assert.True(_store.Reschedule(item.Id, Now.AddDays(9), "#IT2", "moved"));
        var updated = Assert.Single(_store.LoadPending());
        Assert.Equal(Now.AddDays(9), updated.DueAtUtc);
        Assert.Equal("#IT2", updated.Ticket);
        Assert.Equal("moved", updated.Notes);

        _store.Cancel(item.Id, "sualice", Now, "x");
        Assert.False(_store.Reschedule(item.Id, Now.AddDays(1), "#IT3", ""));
    }

    [Fact]
    public void TryClaim_OnlyOneAdminWins_UntilReleasedOrStale()
    {
        var item = _store.Add(Make("jsmith", Now.AddHours(-1)))!;

        Assert.True(_store.TryClaim(item.Id, "sualice", Now));
        Assert.False(_store.TryClaim(item.Id, "subob", Now.AddMinutes(5)));
        Assert.True(_store.TryClaim(item.Id, "sualice", Now.AddMinutes(5)));   // same admin may re-claim

        _store.ReleaseClaim(item.Id, "subob");                                  // not the owner: no effect
        Assert.False(_store.TryClaim(item.Id, "subob", Now.AddMinutes(6)));

        _store.ReleaseClaim(item.Id, "sualice");
        Assert.True(_store.TryClaim(item.Id, "subob", Now.AddMinutes(7)));

        // A crashed admin's claim expires on its own.
        Assert.True(_store.TryClaim(item.Id, "sucarol", Now.AddMinutes(7) + ScheduledTerminationRules.ClaimExpiresAfter + TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void TryClaim_FailsForResolvedOrUnknown()
    {
        var item = _store.Add(Make("jsmith", Now.AddHours(-1)))!;
        _store.Complete(item.Id, "sualice", Now, "done");

        Assert.False(_store.TryClaim(item.Id, "subob", Now));
        Assert.False(_store.TryClaim("nope", "subob", Now));
    }

    [Fact]
    public void CompletePendingForUser_ResolvesAndClearsClaim_AndReportsWhatItResolved()
    {
        var item = _store.Add(Make("jsmith", Now.AddHours(-1)))!;
        _store.TryClaim(item.Id, "sualice", Now);

        var resolved = _store.CompletePendingForUser("JSMITH", "sualice", Now.AddMinutes(30), "Offboarded");

        Assert.NotNull(resolved);
        Assert.Equal(ScheduledTerminationState.Completed, resolved!.State);
        Assert.Empty(_store.LoadPending());
        var kept = Assert.Single(_store.Load());
        Assert.Equal("sualice", kept.ResolvedBy);
        Assert.Equal("", kept.ClaimedBy);
        Assert.Null(_store.CompletePendingForUser("jsmith", "sualice", Now, "again"));
    }

    [Fact]
    public void OldResolvedEntries_ArePrunedOnNextWrite_PendingNeverPruned()
    {
        var old = _store.Add(Make("old", Now.AddDays(-200)))!;
        _store.Cancel(old.Id, "sualice", DateTime.UtcNow - ScheduledTerminationRules.ResolvedHistoryKept - TimeSpan.FromDays(1), "x");
        _store.Add(Make("keep", Now.AddDays(-200)));   // pending and long overdue: must survive

        _store.Add(Make("trigger", Now.AddDays(1)));   // any write runs the prune

        Assert.DoesNotContain(_store.Load(), t => t.Username == "old");
        Assert.Contains(_store.Load(), t => t.Username == "keep");
    }

    [Fact]
    public void CorruptFile_ThrowsAndIsNeverOverwritten()
    {
        File.WriteAllText(_store.FilePath, "{ this is not json");

        Assert.Throws<InvalidDataException>(() => _store.Load());
        Assert.Throws<InvalidDataException>(() => _store.Add(Make("jsmith", Now.AddDays(1))));
        Assert.Equal("{ this is not json", File.ReadAllText(_store.FilePath));
    }

    [Fact]
    public async Task ConcurrentAdds_FromTwoStoreInstances_LoseNothing()
    {
        var other = new ScheduledTerminationStore(_store.FilePath);
        var tasks = Enumerable.Range(0, 12).Select(i => Task.Run(() =>
        {
            var target = i % 2 == 0 ? _store : other;
            Assert.NotNull(target.Add(Make($"user{i}", Now.AddDays(i + 1))));
        })).ToArray();
        await Task.WhenAll(tasks);

        Assert.Equal(12, _store.LoadPending().Count);
    }
}
