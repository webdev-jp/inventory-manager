namespace InventoryManager.Core.Persistence;

public enum InventorySaveResult
{
    Success,

    /// <summary>Mirrors Save-Inventory's own message: "the inventory file has been locked by
    /// another user for too long". The caller's in-memory changes are untouched either way -
    /// this only means the merge-to-disk step didn't happen this time.</summary>
    LockTimedOut,
}
