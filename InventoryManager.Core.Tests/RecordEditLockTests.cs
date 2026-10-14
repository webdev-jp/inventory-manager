using InventoryManager.Core;
using InventoryManager.Core.Models;

namespace InventoryManager.Core.Tests;

public class RecordEditLockTests
{
    private static InventoryItem MakeItem() => new() { Id = 1, DeviceName = "TESTDEV" };

    [Fact]
    public void IsLockedByOther_UnlockedItem_ReturnsFalse()
    {
        var item = MakeItem();
        Assert.False(RecordEditLock.IsLockedByOther(item, "su.admin2", DateTime.UtcNow));
    }

    [Fact]
    public void IsLockedByOther_LockedBySameUser_ReturnsFalse()
    {
        var item = MakeItem();
        var now = DateTime.UtcNow;
        RecordEditLock.Acquire(item, "su.admin1", now);

        Assert.False(RecordEditLock.IsLockedByOther(item, "su.admin1", now));
    }

    [Fact]
    public void IsLockedByOther_LockedBySameUser_CaseInsensitive()
    {
        var item = MakeItem();
        var now = DateTime.UtcNow;
        RecordEditLock.Acquire(item, "SU.ADMIN1", now);

        Assert.False(RecordEditLock.IsLockedByOther(item, "su.admin1", now));
    }

    [Fact]
    public void IsLockedByOther_LockedByDifferentUser_Fresh_ReturnsTrue()
    {
        var item = MakeItem();
        var now = DateTime.UtcNow;
        RecordEditLock.Acquire(item, "su.admin1", now);

        Assert.True(RecordEditLock.IsLockedByOther(item, "su.admin2", now.AddMinutes(1)));
    }

    [Fact]
    public void IsLockedByOther_LockedByDifferentUser_Stale_ReturnsFalse()
    {
        var item = MakeItem();
        var acquiredAt = DateTime.UtcNow;
        RecordEditLock.Acquire(item, "su.admin1", acquiredAt);

        var checkedAt = acquiredAt + RecordEditLock.StaleAfter + TimeSpan.FromSeconds(1);
        Assert.False(RecordEditLock.IsLockedByOther(item, "su.admin2", checkedAt));
    }

    [Fact]
    public void IsLockedByOther_LockedByDifferentUser_JustUnderStaleWindow_ReturnsTrue()
    {
        var item = MakeItem();
        var acquiredAt = DateTime.UtcNow;
        RecordEditLock.Acquire(item, "su.admin1", acquiredAt);

        var checkedAt = acquiredAt + RecordEditLock.StaleAfter - TimeSpan.FromSeconds(1);
        Assert.True(RecordEditLock.IsLockedByOther(item, "su.admin2", checkedAt));
    }

    [Fact]
    public void IsLockedByOther_MalformedTimestamp_FailsOpen_ReturnsFalse()
    {
        var item = MakeItem();
        item.EditLockedBy = "su.admin1";
        item.EditLockedAtUtc = "not-a-real-timestamp";

        Assert.False(RecordEditLock.IsLockedByOther(item, "su.admin2", DateTime.UtcNow));
    }

    [Fact]
    public void Acquire_SetsLockFields()
    {
        var item = MakeItem();
        var now = DateTime.UtcNow;
        RecordEditLock.Acquire(item, "su.admin1", now);

        Assert.Equal("su.admin1", item.EditLockedBy);
        Assert.Equal(now.ToString("o"), item.EditLockedAtUtc);
    }

    [Fact]
    public void Release_ByLockOwner_ClearsLockFields()
    {
        var item = MakeItem();
        RecordEditLock.Acquire(item, "su.admin1", DateTime.UtcNow);

        RecordEditLock.Release(item, "su.admin1");

        Assert.Equal("", item.EditLockedBy);
        Assert.Equal("", item.EditLockedAtUtc);
    }

    [Fact]
    public void Release_ByNonOwner_LeavesLockIntact()
    {
        // Guards against a delayed release call from a session whose own lock already expired
        // and was legitimately taken over by someone else - that new lock must not be clobbered.
        var item = MakeItem();
        RecordEditLock.Acquire(item, "su.admin2", DateTime.UtcNow);

        RecordEditLock.Release(item, "su.admin1");

        Assert.Equal("su.admin2", item.EditLockedBy);
        Assert.NotEqual("", item.EditLockedAtUtc);
    }
}
