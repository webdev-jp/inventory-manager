namespace InventoryManager.Core.Models;

/// <summary>
/// Mirrors the PowerShell build's $script:FieldDefs "Type" string ("text", "dropdown",
/// "dynamicdropdown") - kept as its own type so the grid/dialog code can switch on it instead of
/// comparing strings everywhere.
/// </summary>
public enum FieldType
{
    Text,
    Dropdown,
    DynamicDropdown,
}
