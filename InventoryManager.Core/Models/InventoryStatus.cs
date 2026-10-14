namespace InventoryManager.Core.Models;

/// <summary>
/// One tab from the PowerShell build's $script:Statuses - Key is the value stored in an item's
/// Status field, Label is what the tab shows.
/// </summary>
public sealed record InventoryStatus(string Key, string Label);
