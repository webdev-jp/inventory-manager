namespace InventoryManager.Core.Models;

/// <summary>
/// One entry from the PowerShell build's $script:FieldDefs - order in the list is the display
/// order in the grid and the Add/Edit dialog, same as there. Data-driven on purpose: adding a
/// field means adding one entry here, not touching every place that renders a field.
/// </summary>
public sealed class FieldDefinition
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public required FieldType Type { get; init; }

    /// <summary>Fixed choices for Type == Dropdown. Unused for Text/DynamicDropdown.</summary>
    public string[] Options { get; init; } = [];

    /// <summary>Default value a new item's field starts with (Dropdown fields only).</summary>
    public string Default { get; init; } = "";

    /// <summary>Extra CSV header names (case-insensitive) recognized on import.</summary>
    public string[] Aliases { get; init; } = [];

    /// <summary>Column exists (JSON/CSV round-trip) but is hidden from grid/dialog/search.</summary>
    public bool Hidden { get; init; }

    public bool ReadOnly { get; init; }

    /// <summary>Whether this field's value should also be pushed to the linked AD computer's Description.</summary>
    public bool SyncToAd { get; init; }
}
