using InventoryManager.Core;
using InventoryManager.Core.Models;

namespace InventoryManager.Core.Tests;

public class DeviceGroupingTests
{
    private static InventoryItem MakeItem(int id, string deviceName, string status = "Current") =>
        new() { Id = id, DeviceName = deviceName, Status = status };

    private static readonly HashSet<string> NoneExpanded = new(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void SingleDevice_NoGroupInfo()
    {
        var result = DeviceGrouping.Compute([MakeItem(1, "DEV-1")], NoneExpanded);
        Assert.Empty(result);
    }

    [Fact]
    public void TwoRowsSameDeviceAndStatus_LowestIdIsPrimary()
    {
        var items = new[] { MakeItem(2, "DEV-1"), MakeItem(1, "DEV-1") };
        var result = DeviceGrouping.Compute(items, NoneExpanded);
        Assert.True(result[1].IsPrimary);
        Assert.False(result[2].IsPrimary);
        Assert.Equal(2, result[1].GroupCount);
        Assert.Equal(2, result[2].GroupCount);
    }

    [Fact]
    public void NonPrimaryMembers_CollapsedByDefault()
    {
        var items = new[] { MakeItem(1, "DEV-1"), MakeItem(2, "DEV-1") };
        var result = DeviceGrouping.Compute(items, NoneExpanded);
        Assert.False(result[1].Collapsed);
        Assert.True(result[2].Collapsed);
    }

    [Fact]
    public void ExpandedGroup_NonPrimaryMembersNotCollapsed()
    {
        var items = new[] { MakeItem(1, "DEV-1"), MakeItem(2, "DEV-1") };
        var expanded = new HashSet<string> { DeviceGrouping.GroupKey("Current", "DEV-1") };
        var result = DeviceGrouping.Compute(items, expanded);
        Assert.False(result[2].Collapsed);
    }

    [Fact]
    public void SameDeviceNameDifferentStatus_TreatedAsSeparateGroups()
    {
        var items = new[] { MakeItem(1, "DEV-1", "Current"), MakeItem(2, "DEV-1", "ITStock"), MakeItem(3, "DEV-1", "Current") };
        var result = DeviceGrouping.Compute(items, NoneExpanded);
        // Only the two "Current" rows (1 and 3) form a group; the lone "ITStock" row doesn't.
        Assert.True(result.ContainsKey(1));
        Assert.True(result.ContainsKey(3));
        Assert.False(result.ContainsKey(2));
    }

    [Fact]
    public void BlankDeviceName_NeverGrouped()
    {
        var items = new[] { MakeItem(1, ""), MakeItem(2, "") };
        var result = DeviceGrouping.Compute(items, NoneExpanded);
        Assert.Empty(result);
    }

    [Fact]
    public void GroupKey_IsCaseAndWhitespaceInsensitive()
    {
        Assert.Equal(DeviceGrouping.GroupKey("Current", "DEV-1"), DeviceGrouping.GroupKey(" current ", " dev-1 "));
    }

    [Fact]
    public void ThreeWayGroup_AllGetCorrectCount()
    {
        var items = new[] { MakeItem(3, "DEV-1"), MakeItem(1, "DEV-1"), MakeItem(2, "DEV-1") };
        var result = DeviceGrouping.Compute(items, NoneExpanded);
        Assert.True(result[1].IsPrimary);
        Assert.False(result[2].IsPrimary);
        Assert.False(result[3].IsPrimary);
        Assert.All(result.Values, v => Assert.Equal(3, v.GroupCount));
    }
}
