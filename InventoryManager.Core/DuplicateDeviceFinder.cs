using InventoryManager.Core.Models;

namespace InventoryManager.Core;

public sealed record DuplicateDeviceRow(int Id, string DeviceName, string LastName, string FirstName, string StatusKey, string StatusLabel, string SerialNumber);

/// <summary>Port of the PowerShell build's Show-DuplicateDevicesDialog duplicate-detection pass:
/// groups items by trimmed, case-insensitive Device Name, surfacing every row in any group with
/// more than one member (blank Device Names never count as a match).</summary>
public static class DuplicateDeviceFinder
{
    public static IReadOnlyList<DuplicateDeviceRow> FindDuplicates(IEnumerable<InventoryItem> items)
    {
        var duplicateGroups = items
            .Where(i => !string.IsNullOrWhiteSpace(i.DeviceName))
            .GroupBy(i => i.DeviceName.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1);

        var rows = new List<DuplicateDeviceRow>();
        foreach (var group in duplicateGroups)
        {
            foreach (var item in group)
            {
                var label = InventorySchema.Statuses.FirstOrDefault(s => s.Key == item.Status)?.Label ?? item.Status;
                rows.Add(new DuplicateDeviceRow(item.Id, item.DeviceName, item.LastName, item.FirstName, item.Status, label, item["SerialNumber"]));
            }
        }
        return rows.OrderBy(r => r.DeviceName, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
