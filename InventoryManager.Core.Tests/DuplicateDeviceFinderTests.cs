using InventoryManager.Core;
using InventoryManager.Core.Models;

namespace InventoryManager.Core.Tests;

public class DuplicateDeviceFinderTests
{
    private static InventoryItem MakeItem(int id, string deviceName, string status = "Current") =>
        new() { Id = id, DeviceName = deviceName, Status = status };

    [Fact]
    public void FindDuplicates_NoDuplicates_ReturnsEmpty()
    {
        var items = new[] { MakeItem(1, "LAPTOP-01"), MakeItem(2, "LAPTOP-02") };
        Assert.Empty(DuplicateDeviceFinder.FindDuplicates(items));
    }

    [Fact]
    public void FindDuplicates_CaseAndWhitespaceInsensitiveMatch()
    {
        var items = new[] { MakeItem(1, "laptop-01"), MakeItem(2, " LAPTOP-01 ") };
        var result = DuplicateDeviceFinder.FindDuplicates(items);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void FindDuplicates_BlankDeviceName_NeverCountsAsDuplicate()
    {
        var items = new[] { MakeItem(1, ""), MakeItem(2, "  "), MakeItem(3, "") };
        Assert.Empty(DuplicateDeviceFinder.FindDuplicates(items));
    }

    [Fact]
    public void FindDuplicates_ResolvesStatusLabelAndSortsByDeviceName()
    {
        var items = new[]
        {
            MakeItem(1, "ZZZ-01", status: "ITStock"),
            MakeItem(2, "ZZZ-01", status: "Current"),
            MakeItem(3, "AAA-01"),
            MakeItem(4, "AAA-01"),
        };
        var result = DuplicateDeviceFinder.FindDuplicates(items);
        Assert.Equal(4, result.Count);
        Assert.Equal("AAA-01", result[0].DeviceName);
        Assert.Equal("AAA-01", result[1].DeviceName);
        Assert.Equal("ZZZ-01", result[2].DeviceName);
        Assert.Equal("IT Stock", result.First(r => r.Id == 1).StatusLabel);
        Assert.Equal("Current Inventory", result.First(r => r.Id == 2).StatusLabel);
    }

    [Fact]
    public void FindDuplicates_ThreeWayDuplicate_ReturnsAllThree()
    {
        var items = new[] { MakeItem(1, "SHARED"), MakeItem(2, "SHARED"), MakeItem(3, "SHARED"), MakeItem(4, "UNIQUE") };
        var result = DuplicateDeviceFinder.FindDuplicates(items);
        Assert.Equal(3, result.Count);
    }
}
