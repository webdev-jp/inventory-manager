using System.Data;
using InventoryManager.Core;
using InventoryManager.Core.Models;

namespace InventoryManager.App.Grid;

/// <summary>
/// Projects the in-memory List&lt;InventoryItem&gt; into a System.Data.DataTable purely for
/// DataGridView display - mirrors the PowerShell build's own use of a DataTable/DataView with a
/// RowFilter per tab, which WinForms' grid binds to natively. Grids stay read-only (same as the
/// PowerShell build); edits go through ItemEditDialog against the real InventoryItem list, then
/// the table here gets rebuilt from that authoritative source - this class never writes back.
/// </summary>
public static class InventoryGridBuilder
{
    private static readonly string[] SpecialColumns =
        ["Id", "Status", "ADFlagReason", "ADComputerDisabled", "ADComputerNotFound", "IgnoreHighlight",
         "IgnoreAdSync", "GroupCollapsed", "GroupPrimary", "GroupCount"];

    public static DataTable BuildTable(IEnumerable<InventoryItem> items, IReadOnlySet<string>? expandedGroupKeys = null)
    {
        var itemList = items as IReadOnlyList<InventoryItem> ?? items.ToList();
        var groupInfo = DeviceGrouping.Compute(itemList, expandedGroupKeys ?? new HashSet<string>());

        var table = new DataTable();
        table.Columns.Add("Id", typeof(int));
        foreach (var column in SpecialColumns.Skip(1))
        {
            table.Columns.Add(column, typeof(string));
        }
        foreach (var field in InventorySchema.FieldDefs)
        {
            table.Columns.Add(field.Key, typeof(string));
        }

        foreach (var item in itemList)
        {
            var row = table.NewRow();
            row["Id"] = item.Id;
            row["Status"] = item.Status;
            row["ADFlagReason"] = item.ADFlagReason;
            row["ADComputerDisabled"] = item.ADComputerDisabled;
            row["ADComputerNotFound"] = item.ADComputerNotFound;
            row["IgnoreHighlight"] = item.IgnoreHighlight;
            row["IgnoreAdSync"] = item.IgnoreAdSync;
            if (groupInfo.TryGetValue(item.Id, out var info))
            {
                row["GroupCollapsed"] = info.Collapsed ? "Yes" : "";
                row["GroupPrimary"] = info.IsPrimary ? "Yes" : "";
                row["GroupCount"] = info.GroupCount.ToString();
            }
            else
            {
                row["GroupCollapsed"] = "";
                row["GroupPrimary"] = "";
                row["GroupCount"] = "";
            }
            foreach (var field in InventorySchema.FieldDefs)
            {
                row[field.Key] = item[field.Key];
            }
            table.Rows.Add(row);
        }

        table.PrimaryKey = [table.Columns["Id"]!];
        return table;
    }

    /// <summary>Escapes a value for safe use inside a DataView.RowFilter expression (single quotes doubled).</summary>
    public static string EscapeForRowFilter(string value) => value.Replace("'", "''");
}
