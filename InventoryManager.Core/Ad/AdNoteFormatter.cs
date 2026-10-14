namespace InventoryManager.Core.Ad;

/// <summary>
/// Direct port of the PowerShell build's Format-AdNoteEntry: one consistent timestamp format for
/// every entry stamped into the AD "info" attribute (the Notes box under ADUC's Telephones tab),
/// across Onboarding/Transfer/Offboarding/inventory field-change tracking.
/// </summary>
public static class AdNoteFormatter
{
    /// <summary>12-hour clock with AM/PM (e.g. "9/15/2026 3:45 PM") - Francois explicitly asked for
    /// this over a 24-hour timestamp on 2026-09-15. Was "yyyy-MM-dd HH:mm" before that.</summary>
    public static string FormatEntry(string actionText) => $"[{DateTime.Now:M/d/yyyy h:mm tt}] {actionText}";
}
