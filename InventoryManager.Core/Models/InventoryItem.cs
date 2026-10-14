namespace InventoryManager.Core.Models;

/// <summary>
/// One inventory row. Mirrors the PowerShell build's DataTable row shape: a handful of fixed
/// "special" columns (Id/Status/AD*/IgnoreHighlight) plus every $script:FieldDefs key, stored
/// here as plain strings in a dictionary the same way the PowerShell DataTable stores every
/// FieldDefs column as [string] regardless of what it semantically holds. Kept as a plain POCO
/// (no System.Data, no WinForms) so InventoryStore's merge/diff logic - the highest-risk piece of
/// this rewrite - can be unit tested without spinning up a UI.
/// </summary>
public sealed class InventoryItem
{
    public int Id { get; set; }
    public string Status { get; set; } = "";
    public string ADFlagReason { get; set; } = "";
    public string ADComputerDisabled { get; set; } = "";
    public string ADComputerNotFound { get; set; } = "";
    public string IgnoreHighlight { get; set; } = "";
    /// <summary>"Yes" excludes this row entirely from AdUserMatchCheckService.Run - no
    /// ADFlagReason/ADComputerDisabled/ADComputerNotFound updates, no auto-move to another tab.
    /// For a known-good exception AD Sync would otherwise flag/relocate (e.g. a shared/kiosk
    /// device with no real assigned user, or a computer intentionally left disabled while still in
    /// active use) - Francois, 2026-09-17. Independent of IgnoreHighlight, which only suppresses
    /// row coloring and has no effect on AD Sync's actual writes.</summary>
    public string IgnoreAdSync { get; set; } = "";
    /// <summary>SU account currently editing this record (Edit/Transfer/Offboard), or "" when
    /// free - see RecordEditLock. Persisted through the normal save/merge/refresh pipeline like
    /// every other special column, so every session sees a lock within one periodic refresh.</summary>
    public string EditLockedBy { get; set; } = "";
    /// <summary>UTC timestamp (round-trip "o" format) the lock above was acquired - used to expire
    /// a stale lock left behind by a crashed/force-closed session. See RecordEditLock.StaleAfter.</summary>
    public string EditLockedAtUtc { get; set; } = "";

    private readonly Dictionary<string, string> _fields = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Read/write access to a FieldDefinition's value by Key. Unknown keys read back "".</summary>
    public string this[string fieldKey]
    {
        get => _fields.TryGetValue(fieldKey, out var value) ? value : "";
        set => _fields[fieldKey] = value ?? "";
    }

    // Convenience accessors for the field keys touched constantly outside the generic
    // FieldDefinition loops (identity/AD-linking fields) - everything else goes through the
    // indexer above, same as the PowerShell version reaches most fields via $row[$f.Key] but
    // reaches these same few directly by name throughout.
    public string DeviceName { get => this["DeviceName"]; set => this["DeviceName"] = value; }
    public string FirstName { get => this["FirstName"]; set => this["FirstName"] = value; }
    public string LastName { get => this["LastName"]; set => this["LastName"] = value; }
    public string Username { get => this["Username"]; set => this["Username"] = value; }
    public string Description { get => this["Description"]; set => this["Description"] = value; }

    public string FullName => $"{FirstName} {LastName}".Trim();

    /// <summary>Snapshot of every field currently set, keyed by FieldDefinition.Key.</summary>
    public IReadOnlyDictionary<string, string> Fields => _fields;

    public InventoryItem Clone()
    {
        var clone = new InventoryItem
        {
            Id = Id,
            Status = Status,
            ADFlagReason = ADFlagReason,
            ADComputerDisabled = ADComputerDisabled,
            ADComputerNotFound = ADComputerNotFound,
            IgnoreHighlight = IgnoreHighlight,
            IgnoreAdSync = IgnoreAdSync,
            EditLockedBy = EditLockedBy,
            EditLockedAtUtc = EditLockedAtUtc,
        };
        foreach (var (key, value) in _fields)
        {
            clone[key] = value;
        }
        return clone;
    }
}
