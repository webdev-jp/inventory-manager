using InventoryManager.Core.Models;

namespace InventoryManager.Core;

/// <summary>
/// Per-record "checked out for editing" lock, added per Francois's explicit request: two admins
/// (each running their own session against the shared inventory.json) must not both open Edit/
/// Transfer/Offboard on the SAME row at once - whoever saved last would otherwise silently
/// overwrite the other's change. Distinct from InventoryLock (the file-level exclusive lock held
/// only for the brief duration of a Save() read-merge-write cycle) - this one is held for as long
/// as a SPECIFIC ROW is open in an editor, and is visible to every other session via the item's
/// own EditLockedBy/EditLockedAtUtc fields, which round-trip through the normal save/merge/
/// periodic-refresh pipeline like any other field.
///
/// A stale lock (left behind by a crashed or force-closed session) expires automatically after
/// <see cref="StaleAfter"/> rather than requiring manual intervention - the same reasoning
/// InventoryLock's own staleness timeout uses for the file lock.
/// </summary>
public static class RecordEditLock
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(30);

    /// <summary>True if someone OTHER than <paramref name="suUsername"/> currently holds a
    /// non-stale lock on this item. A malformed or unparsable timestamp is treated as "not
    /// locked" (fails open) - this is a courtesy against lost edits, not a security boundary, so a
    /// corrupt field should never be able to permanently block editing a record.</summary>
    public static bool IsLockedByOther(InventoryItem item, string suUsername, DateTime utcNow)
    {
        if (string.IsNullOrWhiteSpace(item.EditLockedBy))
        {
            return false;
        }
        if (string.Equals(item.EditLockedBy, suUsername, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (!DateTime.TryParse(item.EditLockedAtUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var lockedAtUtc))
        {
            return false;
        }
        return utcNow - lockedAtUtc < StaleAfter;
    }

    public static void Acquire(InventoryItem item, string suUsername, DateTime utcNow)
    {
        item.EditLockedBy = suUsername;
        item.EditLockedAtUtc = utcNow.ToString("o");
    }

    /// <summary>Only releases if this item isn't currently locked by someone else's still-fresh
    /// lock - guards against a delayed release call clobbering a lock a different session has since
    /// legitimately acquired (e.g. this session's own lock already expired and was taken over).</summary>
    public static void Release(InventoryItem item, string suUsername)
    {
        if (string.Equals(item.EditLockedBy, suUsername, StringComparison.OrdinalIgnoreCase))
        {
            item.EditLockedBy = "";
            item.EditLockedAtUtc = "";
        }
    }
}
