using InventoryManager.Core;
using InventoryManager.Core.Models;
using InventoryManager.Core.Transfer;

namespace InventoryManager.Core.Tests;

public class UserLimboAndPeopleTests
{
    private static InventoryItem Row(int id, string device, string user = "", string first = "", string last = "", string status = "Current", string cell = "", string model = "")
    {
        var item = new InventoryItem { Id = id, DeviceName = device, Username = user, FirstName = first, LastName = last, Status = status };
        item["CellPhone"] = cell;
        item["DeviceModel"] = model;
        return item;
    }

    // ---- UserLimbo ----

    [Fact]
    public void MoveUserToLimbo_CreatesNoPcRow_WithThePersonsFields_AndClearsTheDevice()
    {
        var device = Row(1, "DEV-1", "jsmith", "John", "Smith", cell: "914-555-0100", model: "iPhone 14");
        device.Description = "John Smith***";
        device.ADFlagReason = "No matching AD user found";
        var items = new List<InventoryItem> { device };

        var result = UserLimbo.MoveUserToLimbo(items, device);

        Assert.True(result.Created);
        Assert.Equal(2, items.Count);
        var limbo = result.LimboRow;
        Assert.Equal("No PC", limbo.DeviceName);
        Assert.Equal("Current", limbo.Status);
        Assert.Equal("jsmith", limbo.Username);
        Assert.Equal("John", limbo.FirstName);
        Assert.Equal("Smith", limbo.LastName);
        Assert.Equal("914-555-0100", limbo["CellPhone"]);
        Assert.Equal("iPhone 14", limbo["DeviceModel"]);
        Assert.Equal(2, limbo.Id);

        Assert.Equal("DEV-1", device.DeviceName);
        Assert.Equal("", device.Username);
        Assert.Equal("", device.FirstName);
        Assert.Equal("", device.LastName);
        Assert.Equal("", device.Description);
        Assert.Equal("", device["CellPhone"]);
        Assert.Equal("", device["DeviceModel"]);
        Assert.Equal("", device.ADFlagReason);
    }

    [Fact]
    public void MoveUserToLimbo_KeepsTheDevicesHardwareFields()
    {
        var device = Row(1, "DEV-1", "jsmith", "John", "Smith");
        device["SerialNumber"] = "SN123";
        device["Model"] = "Latitude 7440";

        UserLimbo.MoveUserToLimbo([device], device);

        Assert.Equal("SN123", device["SerialNumber"]);
        Assert.Equal("Latitude 7440", device["Model"]);
    }

    [Fact]
    public void MoveUserToLimbo_ReusesTheUsersExistingNoPcRow_AndFillsOnlyBlankPhoneFields()
    {
        var device = Row(1, "DEV-1", "jsmith", "John", "Smith", cell: "914-555-0100", model: "iPhone 14");
        var existing = Row(7, "No PC", "JSmith", "John", "Smith", cell: "914-555-9999");
        var items = new List<InventoryItem> { device, existing };

        var result = UserLimbo.MoveUserToLimbo(items, device);

        Assert.False(result.Created);
        Assert.Same(existing, result.LimboRow);
        Assert.Equal(2, items.Count);
        Assert.Equal("914-555-9999", existing["CellPhone"]);   // already had one - not overwritten
        Assert.Equal("iPhone 14", existing["DeviceModel"]);    // was blank - filled in
    }

    [Fact]
    public void MoveUserToLimbo_AUserWithOnlyANameIsMatchedToTheirNoPcRowByName()
    {
        var device = Row(1, "DEV-1", "", "John", "Smith");
        var existing = Row(7, "No PC", "", "john", "SMITH");
        var items = new List<InventoryItem> { device, existing };

        var result = UserLimbo.MoveUserToLimbo(items, device);

        Assert.False(result.Created);
        Assert.Same(existing, result.LimboRow);
    }

    [Fact]
    public void MoveUserToLimbo_ADifferentPersonsNoPcRowIsNotReused()
    {
        var device = Row(1, "DEV-1", "jsmith", "John", "Smith");
        var someoneElse = Row(7, "No PC", "msmith", "Mary", "Smith");
        var items = new List<InventoryItem> { device, someoneElse };

        var result = UserLimbo.MoveUserToLimbo(items, device);

        Assert.True(result.Created);
        Assert.NotSame(someoneElse, result.LimboRow);
        Assert.Equal(3, items.Count);
    }

    [Fact]
    public void MoveUserToLimbo_RefusesANoPcRow_AndADeviceWithNoUser()
    {
        var noPc = Row(1, "No PC", "jsmith", "John", "Smith");
        var empty = Row(2, "DEV-2");

        Assert.Throws<ArgumentException>(() => UserLimbo.MoveUserToLimbo([noPc, empty], noPc));
        Assert.Throws<ArgumentException>(() => UserLimbo.MoveUserToLimbo([noPc, empty], empty));
    }

    [Fact]
    public void HasAssignedUser_TrueForAUsernameOrJustAName_FalseForNeither()
    {
        Assert.True(UserLimbo.HasAssignedUser(Row(1, "D", "jsmith")));
        Assert.True(UserLimbo.HasAssignedUser(Row(1, "D", first: "John")));
        Assert.True(UserLimbo.HasAssignedUser(Row(1, "D", last: "Smith")));
        Assert.False(UserLimbo.HasAssignedUser(Row(1, "D")));
    }

    // ---- PeopleView ----

    [Fact]
    public void People_GroupedByUsername_WithEveryRealDevice_AndStatusLabels()
    {
        var items = new[]
        {
            Row(1, "DEV-1", "jsmith", "John", "Smith"),
            Row(2, "DEV-2", "JSMITH", "John", "Smith", status: "DisabledDevices"),
            Row(3, "DEV-3", "ajones", "Alice", "Jones"),
        };

        var people = PeopleView.Build(items);

        Assert.Equal(2, people.Count);
        Assert.Equal("ajones", people[0].Username);                       // Jones before Smith
        var smith = people[1];
        Assert.Equal(["DEV-1", "DEV-2"], smith.Devices.Select(d => d.DeviceName));
        Assert.Equal(["Current Inventory", "Disabled Devices"], smith.Devices.Select(d => d.StatusLabel));
        Assert.False(smith.InLimbo);
    }

    [Fact]
    public void People_ANoPcRowMakesAPersonWhoIsInLimbo()
    {
        var people = PeopleView.Build([Row(1, "No PC", "jsmith", "John", "Smith", cell: "914-555-0100")]);

        var p = Assert.Single(people);
        Assert.True(p.InLimbo);
        Assert.Empty(p.Devices);
        Assert.Equal("914-555-0100", p.CellPhone);
    }

    [Fact]
    public void People_APersonWithADeviceAndANoPcRow_IsNotInLimbo_AndShowsOneDevice()
    {
        var people = PeopleView.Build([Row(1, "DEV-1", "jsmith", "John", "Smith"), Row(2, "No PC", "jsmith", "John", "Smith")]);

        var p = Assert.Single(people);
        Assert.False(p.InLimbo);
        Assert.Single(p.Devices);
    }

    [Fact]
    public void People_ANameOnlyRowJoinsTheUsernameGroupWithTheSameName_ElseStandsAlone()
    {
        var items = new[]
        {
            Row(1, "DEV-1", "jsmith", "John", "Smith"),
            Row(2, "DEV-2", "", "john", "SMITH"),
            Row(3, "DEV-3", "", "Pat", "Lee"),
        };

        var people = PeopleView.Build(items);

        Assert.Equal(2, people.Count);
        var smith = people.Single(p => p.LastName == "Smith");
        Assert.Equal(["DEV-1", "DEV-2"], smith.Devices.Select(d => d.DeviceName));
        var lee = people.Single(p => p.LastName == "Lee");
        Assert.Equal("", lee.Username);
        Assert.Single(lee.Devices);
    }

    [Fact]
    public void People_UnassignedDevicesAreNotPeople_AndTheSameDeviceIsNotListedTwice()
    {
        var items = new[]
        {
            Row(1, "BENCH-1"),                                    // IT Stock device, nobody attached
            Row(2, "DEV-1", "jsmith", "John", "Smith"),
            Row(3, "DEV-1", "jsmith", "John", "Smith"),           // duplicate record of the same device
        };

        var people = PeopleView.Build(items);

        var p = Assert.Single(people);
        Assert.Single(p.Devices);
    }
}
