using InventoryManager.Core;
using InventoryManager.Core.Models;

namespace InventoryManager.Core.Tests;

public class InventoryLookupTests
{
    private static InventoryItem MakeItem(int id, string deviceName, string lastName = "", string firstName = "") =>
        new() { Id = id, DeviceName = deviceName, LastName = lastName, FirstName = firstName };

    [Fact]
    public void FindByName_CaseInsensitiveMatch()
    {
        var items = new[] { MakeItem(1, "DEV-1", "Smith", "Jane") };
        var result = InventoryLookup.FindByName(items, "SMITH", "jane");
        Assert.Single(result);
    }

    [Fact]
    public void FindByName_BlankLastOrFirst_ReturnsEmpty()
    {
        var items = new[] { MakeItem(1, "DEV-1", "Smith", "Jane") };
        Assert.Empty(InventoryLookup.FindByName(items, "", "Jane"));
        Assert.Empty(InventoryLookup.FindByName(items, "Smith", ""));
    }

    [Fact]
    public void FindByName_MultipleDevicesForSamePerson_ReturnsAll()
    {
        var items = new[] { MakeItem(1, "DEV-1", "Smith", "Jane"), MakeItem(2, "DEV-2", "Smith", "Jane") };
        Assert.Equal(2, InventoryLookup.FindByName(items, "Smith", "Jane").Count);
    }

    [Fact]
    public void FindComputersNotInInventory_ExcludesExistingNames()
    {
        var items = new[] { MakeItem(1, "DEV-1") };
        var result = InventoryLookup.FindComputersNotInInventory(items, ["DEV-1", "DEV-2"]);
        Assert.Equal(["DEV-2"], result);
    }

    [Fact]
    public void FindComputersNotInInventory_CaseAndWhitespaceInsensitive()
    {
        var items = new[] { MakeItem(1, " dev-1 ") };
        var result = InventoryLookup.FindComputersNotInInventory(items, ["DEV-1"]);
        Assert.Empty(result);
    }

    [Fact]
    public void FindComputersNotInInventory_SortsResultsAlphabetically()
    {
        var result = InventoryLookup.FindComputersNotInInventory([], ["ZZZ", "AAA", "MMM"]);
        Assert.Equal(["AAA", "MMM", "ZZZ"], result);
    }
}
