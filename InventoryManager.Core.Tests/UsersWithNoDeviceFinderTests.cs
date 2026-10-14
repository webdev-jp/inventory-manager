using InventoryManager.Core.Ad;
using InventoryManager.Core.Models;

namespace InventoryManager.Core.Tests;

public class UsersWithNoDeviceFinderTests
{
    private static InventoryItem MakeItem(int id, string lastName = "", string firstName = "", string username = "", string status = "Current")
    {
        return new InventoryItem { Id = id, DeviceName = $"DEV-{id}", LastName = lastName, FirstName = firstName, Username = username, Status = status };
    }

    [Fact]
    public void UserAnchoredByUsername_OnCurrentRow_CountsAsHasDevice()
    {
        var items = new[] { MakeItem(1, username: "jsmith") };
        var adUsers = new[] { new AdUserMatchInfo("jsmith", "John", "Smith", true) };

        var result = UsersWithNoDeviceFinder.Find(items, adUsers);

        Assert.Empty(result);
    }

    [Fact]
    public void UserMatchedByNameOnly_NeverRunThroughMatchUser_StillCountsAsHasDevice()
    {
        var items = new[] { MakeItem(1, lastName: "Smith", firstName: "John", username: "") };
        var adUsers = new[] { new AdUserMatchInfo("jsmith", "John", "Smith", true) };

        var result = UsersWithNoDeviceFinder.Find(items, adUsers);

        Assert.Empty(result);
    }

    [Fact]
    public void NoMatchingRowAtAll_ReturnedAsNoDevice()
    {
        var items = Array.Empty<InventoryItem>();
        var adUsers = new[] { new AdUserMatchInfo("jsmith", "John", "Smith", true) };

        var result = UsersWithNoDeviceFinder.Find(items, adUsers);

        var user = Assert.Single(result);
        Assert.Equal("jsmith", user.Username);
        Assert.Equal("John", user.FirstName);
        Assert.Equal("Smith", user.LastName);
    }

    [Theory]
    [InlineData("ITStock")]
    [InlineData("DisabledDevices")]
    public void RowOnExcludedStatus_DoesNotCountAsHasDevice(string status)
    {
        // ITStock is unassigned bench stock; DisabledDevices (the Terminated successor) is where a
        // device lands once its person has left - neither represents a CURRENT assignment.
        var items = new[] { MakeItem(1, lastName: "Smith", firstName: "John", username: "jsmith", status: status) };
        var adUsers = new[] { new AdUserMatchInfo("jsmith", "John", "Smith", true) };

        var result = UsersWithNoDeviceFinder.Find(items, adUsers);

        Assert.Single(result);
    }

    [Fact]
    public void RowOnComputerNotInAd_StillCountsAsHasDevice()
    {
        // The DEVICE isn't matched to AD, but the person genuinely already has one assigned - a
        // data-quality issue about the device, not a "this person needs a device" case.
        var items = new[] { MakeItem(1, lastName: "Smith", firstName: "John", username: "jsmith", status: "ComputerNotInAD") };
        var adUsers = new[] { new AdUserMatchInfo("jsmith", "John", "Smith", true) };

        var result = UsersWithNoDeviceFinder.Find(items, adUsers);

        Assert.Empty(result);
    }

    [Fact]
    public void DisabledAdAccount_NeverIncluded()
    {
        var items = Array.Empty<InventoryItem>();
        var adUsers = new[] { new AdUserMatchInfo("jsmith", "John", "Smith", false) };

        var result = UsersWithNoDeviceFinder.Find(items, adUsers);

        Assert.Empty(result);
    }

    [Fact]
    public void ServiceAccountWithNoNameFields_Skipped()
    {
        var items = Array.Empty<InventoryItem>();
        var adUsers = new[] { new AdUserMatchInfo("svc-backup", "", "", true) };

        var result = UsersWithNoDeviceFinder.Find(items, adUsers);

        Assert.Empty(result);
    }

    [Fact]
    public void SortedByLastNameThenFirstName()
    {
        var items = Array.Empty<InventoryItem>();
        var adUsers = new[]
        {
            new AdUserMatchInfo("bsmith", "Bob", "Smith", true),
            new AdUserMatchInfo("ajones", "Alice", "Jones", true),
            new AdUserMatchInfo("asmith", "Alice", "Smith", true),
        };

        var result = UsersWithNoDeviceFinder.Find(items, adUsers);

        Assert.Equal(["ajones", "asmith", "bsmith"], result.Select(r => r.Username));
    }
}
