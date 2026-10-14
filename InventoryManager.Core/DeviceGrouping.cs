using InventoryManager.Core.Models;

namespace InventoryManager.Core;

public sealed record DeviceGroupInfo(bool IsPrimary, int GroupCount, bool Collapsed);

/// <summary>
/// Direct port of the PowerShell build's Update-DuplicateGroups: when multiple users share one
/// device (grouped by Status + Device Name, not Device Name alone - so a device recorded
/// inconsistently across two tabs stays visible in both instead of silently collapsing away a real
/// data-quality problem), the lowest-Id row becomes the "primary" (always visible, gets the
/// expand/collapse arrow), and the rest collapse away unless the caller has that group's key in
/// <paramref name="expandedGroupKeys"/>. Purely computed from current data + in-memory expand
/// state - nothing here is persisted to inventory.json.
/// </summary>
public static class DeviceGrouping
{
    public static string GroupKey(string status, string deviceName) =>
        $"{status.Trim().ToLowerInvariant()}|{deviceName.Trim().ToLowerInvariant()}";

    public static IReadOnlyDictionary<int, DeviceGroupInfo> Compute(IEnumerable<InventoryItem> items, IReadOnlySet<string> expandedGroupKeys)
    {
        var result = new Dictionary<int, DeviceGroupInfo>();
        var groups = items
            .Where(i => !string.IsNullOrWhiteSpace(i.DeviceName))
            .GroupBy(i => GroupKey(i.Status, i.DeviceName));

        foreach (var group in groups)
        {
            var members = group.OrderBy(i => i.Id).ToList();
            if (members.Count <= 1)
            {
                continue;
            }
            var expanded = expandedGroupKeys.Contains(group.Key);
            for (var i = 0; i < members.Count; i++)
            {
                result[members[i].Id] = new DeviceGroupInfo(
                    IsPrimary: i == 0,
                    GroupCount: members.Count,
                    Collapsed: i > 0 && !expanded);
            }
        }
        return result;
    }
}
