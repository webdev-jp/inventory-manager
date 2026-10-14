using InventoryManager.Core.Models;

namespace InventoryManager.Core;

/// <summary>
/// Direct port of InventoryManager.ps1's $script:FieldDefs and $script:Statuses. Order here is
/// the display order in the grid and the Add/Edit dialog, same as there - keep it that way when
/// adding fields.
/// </summary>
public static class InventorySchema
{
    public static readonly IReadOnlyList<FieldDefinition> FieldDefs =
    [
        new FieldDefinition { Key = "DeviceName", Label = "Device Name", Type = FieldType.Text, Aliases = ["Device"] },
        new FieldDefinition { Key = "LastName", Label = "Last Name", Type = FieldType.Text },
        new FieldDefinition { Key = "FirstName", Label = "First Name", Type = FieldType.Text },
        new FieldDefinition { Key = "Model", Label = "Model", Type = FieldType.Text },
        new FieldDefinition { Key = "SerialNumber", Label = "Serial #", Type = FieldType.Text, Aliases = ["Serial"] },
        new FieldDefinition { Key = "Domain", Label = "Domain", Type = FieldType.Dropdown, Options = ["CONTOSO", "Off-Domain"], Default = "CONTOSO", Aliases = ["Domain?"] },
        new FieldDefinition { Key = "ESET", Label = "ESET", Type = FieldType.Dropdown, Options = ["Installed", "Not installed"], Default = "Installed", Aliases = ["ESET on Laptop?"] },
        new FieldDefinition { Key = "AdminProfile", Label = "Admin Profile", Type = FieldType.Dropdown, Options = ["CorpAdmin", "OrgAdmin", "Other"], Default = "CorpAdmin" },
        new FieldDefinition { Key = "Splashtop", Label = "Splashtop", Type = FieldType.Dropdown, Options = ["Yes", "No"], Default = "Yes", Aliases = ["Splashtop installed?"] },
        new FieldDefinition { Key = "Docking", Label = "Docking", Type = FieldType.Dropdown, Options = ["Yes", "No"], Default = "No" },
        new FieldDefinition { Key = "SecondMonitor", Label = "2nd Monitor", Type = FieldType.Dropdown, Options = ["Yes", "No"], Default = "No", Aliases = ["2nd screen"] },
        new FieldDefinition { Key = "PortableMonitor", Label = "Portable Mon.", Type = FieldType.Dropdown, Options = ["Yes", "No"], Default = "No", Aliases = ["Portable screen"] },
        new FieldDefinition { Key = "Topaz", Label = "Topaz", Type = FieldType.Dropdown, Options = ["Yes", "No"], Default = "No" },
        new FieldDefinition { Key = "CellPhone", Label = "Cellphone #", Type = FieldType.Text, Aliases = ["Wireless Number"] },
        new FieldDefinition { Key = "DeviceModel", Label = "Device Model", Type = FieldType.DynamicDropdown, Aliases = ["Mobile Device Model"] },
        new FieldDefinition { Key = "Description", Label = "Description", Type = FieldType.Text, SyncToAd = true },
        new FieldDefinition { Key = "Username", Label = "Username", Type = FieldType.Text, ReadOnly = true },
    ];

    /// <summary>FieldDefs minus Hidden ones - what the Add/Edit dialog, search, and CSV export show.</summary>
    public static readonly IReadOnlyList<FieldDefinition> VisibleFieldDefs =
        FieldDefs.Where(f => !f.Hidden).ToList();

    /// <summary>
    /// The small subset of fields shown as grid columns, in display order, with a grid-only header
    /// override where it differs from the field's normal Label. Everything else - every other
    /// field, plus Id/Status/ADFlagReason/ADComputerDisabled/ADComputerNotFound/IgnoreHighlight -
    /// stays out of the grid entirely; the full record is still visible via double-click/right-
    /// click Edit, which uses VisibleFieldDefs and every field's normal Label unchanged.
    /// </summary>
    public static readonly IReadOnlyList<(string Key, string Header)> GridColumns =
    [
        ("DeviceName", "Device"),
        ("LastName", "Last Name"),
        ("FirstName", "First Name"),
        ("Model", "Laptop Model"),
        ("SerialNumber", "Serial #"),
        ("Domain", "Domain"),
        ("ESET", "ESET Profile"),
        ("Splashtop", "Splashtop Profile"),
        ("CellPhone", "Cellphone #"),
        ("DeviceModel", "Cellphone Model"),
        ("Description", "Description"),
        ("Username", "Username"),
    ];

    public static readonly IReadOnlyList<InventoryStatus> Statuses =
    [
        new InventoryStatus("Current", "Current Inventory"),
        new InventoryStatus("ITStock", "IT Stock"),
        new InventoryStatus("DisabledDevices", "Disabled Devices"),
        new InventoryStatus("ComputerNotInAD", "Computer Not in AD"),
        // Francois, 2026-09-24: "so I can separate the servers from the list of
        // computers/items" - deliberately exempt from the automatic AD enable/disable +
        // Disabled-Computers-OU-relocation that every other Move to/from triggers (see
        // MainForm.MoveSelectedItems) - "the items are not in Current Inventory... they should
        // not be affected." A server's AD lifecycle isn't tied to a person the way a laptop's is.
        new InventoryStatus("Servers", "Servers"),
    ];

    /// <summary>Synthetic Device Name for an assigned-user-only row (CSV import, Users with no
    /// Devices import). It is NOT a computer name - every AD computer lookup must treat it the same
    /// as a blank Device Name. Before 2026-10-01 AD Sync looked "No PC" up as a real computer, moved
    /// 153 freshly-imported placeholder rows to Computer Not in AD, and the resulting per-row audit
    /// mirror wrote to 146 AD accounts' Notes (service and admin accounts included).</summary>
    public const string NoDevicePlaceholder = "No PC";

    /// <summary>True for a Device Name that doesn't name a real computer: blank, or the
    /// <see cref="NoDevicePlaceholder"/>.</summary>
    public static bool IsNoDevice(string? deviceName) =>
        string.IsNullOrWhiteSpace(deviceName) ||
        string.Equals(deviceName.Trim(), NoDevicePlaceholder, StringComparison.OrdinalIgnoreCase);

    public static string GetFieldDefault(FieldDefinition field) =>
        field.Type == FieldType.Dropdown ? field.Default : "";

    /// <summary>
    /// Live options for a DynamicDropdown field - whatever values already exist in the inventory
    /// right now, not a fixed list, so it grows on its own as new ones get typed in.
    /// </summary>
    public static IReadOnlyList<string> GetDistinctFieldValues(IEnumerable<InventoryItem> items, string fieldKey)
    {
        var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            var value = item[fieldKey].Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                set.Add(value);
            }
        }
        return set.ToList();
    }

    public static int GetNextId(IEnumerable<InventoryItem> items)
    {
        var ids = items.Select(i => i.Id).ToList();
        return ids.Count == 0 ? 1 : ids.Max() + 1;
    }
}
