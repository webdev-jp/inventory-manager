using InventoryManager.Core.Models;

namespace InventoryManager.Core;

/// <summary>Direct port of the PowerShell build's Export-ItemsCsv: one column per
/// VisibleFieldDefs entry (hidden fields never round-trip through CSV), headers are the field
/// Keys (matching the PS build's PSCustomObject property names).</summary>
public static class InventoryCsvExporter
{
    public static string Export(IEnumerable<InventoryItem> items)
    {
        var fields = InventorySchema.VisibleFieldDefs;
        var sb = new System.Text.StringBuilder();
        sb.Append(Csv.WriteRow(fields.Select(f => f.Key))).Append("\r\n");
        foreach (var item in items)
        {
            sb.Append(Csv.WriteRow(fields.Select(f => item[f.Key]))).Append("\r\n");
        }
        return sb.ToString();
    }
}
