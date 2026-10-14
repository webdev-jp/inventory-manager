using InventoryManager.Core.Ad;
using InventoryManager.Core.Models;

namespace InventoryManager.Core.Tests;

public class AdUserMatchCheckServiceTests
{
    private const string DisabledOu = "OU=Disabled Computers,OU=Domain Computers,DC=contoso,DC=local";

    private static InventoryItem MakeItem(int id, string deviceName = "DEV-1", string lastName = "", string firstName = "", string username = "", string status = "Current", string domain = "CONTOSO")
    {
        var item = new InventoryItem { Id = id, DeviceName = deviceName, LastName = lastName, FirstName = firstName, Username = username, Status = status };
        item["Domain"] = domain;
        return item;
    }

    [Theory]
    [InlineData("No PC")]
    [InlineData("no pc")]
    [InlineData(" No PC ")]
    public void NoPcPlaceholder_IsNeverComputerNotFound(string deviceName)
    {
        // 2026-09-30 incident: 153 imported "No PC" rows were moved to Computer Not in AD.
        var item = MakeItem(1, deviceName: deviceName, lastName: "Administrator", firstName: "Teams", username: "teamsadmin");
        var users = new[] { new AdUserMatchInfo("teamsadmin", "Teams", "Administrator", true) };

        var result = AdUserMatchCheckService.Run([item], users, [], DisabledOu);

        Assert.Equal("Current", item.Status);
        Assert.Equal("", item.ADComputerNotFound);
        Assert.Equal(0, result.AutoMovedComputerNotInAdCount);
        Assert.Empty(result.Changes);
    }

    [Fact]
    public void UsernameAnchored_MatchedAndEnabled_ClearsFlagAndRefreshesNames()
    {
        var item = MakeItem(1, username: "jsmith", lastName: "Old", firstName: "Name");
        var users = new[] { new AdUserMatchInfo("jsmith", "Jane", "Smith", true) };
        var computers = new[] { new AdComputerMatchInfo("DEV-1", "CN=DEV-1,OU=Domain Computers,DC=x", true) };

        AdUserMatchCheckService.Run([item], users, computers, DisabledOu);

        Assert.Equal("", item.ADFlagReason);
        Assert.Equal("Jane", item.FirstName);
        Assert.Equal("Smith", item.LastName);
    }

    [Fact]
    public void UsernameAnchored_MatchedButDisabled_FlagsAccountDisabled()
    {
        var item = MakeItem(1, username: "jsmith");
        var users = new[] { new AdUserMatchInfo("jsmith", "Jane", "Smith", false) };
        var computers = new[] { new AdComputerMatchInfo("DEV-1", "CN=DEV-1,OU=x", true) };

        var result = AdUserMatchCheckService.Run([item], users, computers, DisabledOu);

        Assert.Equal("AD account disabled", item.ADFlagReason);
        Assert.Equal(1, result.DisabledDeviceCount);
    }

    [Fact]
    public void UsernameAnchored_NoMatch_FlagsNotFound()
    {
        var item = MakeItem(1, username: "ghost");
        var result = AdUserMatchCheckService.Run([item], [], [new AdComputerMatchInfo("DEV-1", "CN=x", true)], DisabledOu);

        Assert.Equal("No matching AD user found", item.ADFlagReason);
        Assert.Equal(1, result.NotFoundDeviceCount);
    }

    [Fact]
    public void NameBased_NoNameAtAll_ClearsFlagWithoutMatching()
    {
        var item = MakeItem(1);
        var result = AdUserMatchCheckService.Run([item], [], [new AdComputerMatchInfo("DEV-1", "CN=x", true)], DisabledOu);
        Assert.Equal("", item.ADFlagReason);
        Assert.Equal(0, result.NotFoundDeviceCount);
    }

    [Fact]
    public void NameBased_EnabledMatch_ClearsFlag()
    {
        var item = MakeItem(1, lastName: "Smith", firstName: "Jane");
        var users = new[] { new AdUserMatchInfo("jsmith", "Jane", "Smith", true) };
        AdUserMatchCheckService.Run([item], users, [new AdComputerMatchInfo("DEV-1", "CN=x", true)], DisabledOu);
        Assert.Equal("", item.ADFlagReason);
    }

    [Fact]
    public void NameBased_DisabledMatch_FlagsAccountDisabled()
    {
        var item = MakeItem(1, lastName: "Smith", firstName: "Jane");
        var users = new[] { new AdUserMatchInfo("jsmith", "Jane", "Smith", false) };
        var result = AdUserMatchCheckService.Run([item], users, [new AdComputerMatchInfo("DEV-1", "CN=x", true)], DisabledOu);
        Assert.Equal("AD account disabled", item.ADFlagReason);
        Assert.Equal(1, result.DisabledDeviceCount);
    }

    [Fact]
    public void ComputerNotFound_AutoMovesToComputerNotInAd()
    {
        var item = MakeItem(1, deviceName: "GHOST-DEVICE");
        var result = AdUserMatchCheckService.Run([item], [], [], DisabledOu);

        Assert.Equal("ComputerNotInAD", item.Status);
        Assert.Equal("No matching AD computer object found", item.ADComputerNotFound);
        Assert.Equal(1, result.AutoMovedComputerNotInAdCount);
        Assert.Equal(1, result.ComputerNotFoundCount);
    }

    [Fact]
    public void UserNotFound_ComputerFound_FlagsButDoesNotMove()
    {
        // The "User Not in AD" tab was removed 2026-09-23 - a user that doesn't resolve now just
        // gets flagged (drives the red row highlight) and is left wherever it already was.
        var item = MakeItem(1, deviceName: "DEV-1", username: "ghost");
        var computers = new[] { new AdComputerMatchInfo("DEV-1", "CN=x", true) };
        AdUserMatchCheckService.Run([item], [], computers, DisabledOu);

        Assert.Equal("Current", item.Status);
        Assert.Equal("No matching AD user found", item.ADFlagReason);
    }

    [Fact]
    public void ComputerAxis_TakesPriorityOverUserAxis_WhenBothFail()
    {
        var item = MakeItem(1, deviceName: "GHOST-DEVICE", username: "ghost");
        var result = AdUserMatchCheckService.Run([item], [], [], DisabledOu);

        // Computer-not-found wins the auto-move even though the user also doesn't resolve.
        Assert.Equal("ComputerNotInAD", item.Status);
        Assert.Equal(1, result.AutoMovedComputerNotInAdCount);
    }

    [Fact]
    public void OffDomain_NeverAutoMovedToComputerNotInAd()
    {
        var item = MakeItem(1, deviceName: "GHOST-DEVICE", username: "ghost", domain: "Off-Domain");
        var result = AdUserMatchCheckService.Run([item], [], [], DisabledOu);

        Assert.Equal("Current", item.Status);
        Assert.Equal(0, result.AutoMovedComputerNotInAdCount);
        // Flags themselves are still stamped even though the auto-move is skipped.
        Assert.Equal("No matching AD computer object found", item.ADComputerNotFound);
    }

    [Fact]
    public void AlreadyInComputerNotInAd_NotReMoved_ButStillReEvaluated()
    {
        var item = MakeItem(1, deviceName: "DEV-1", status: "ComputerNotInAD");
        var computers = new[] { new AdComputerMatchInfo("DEV-1", "CN=x", true) };
        var result = AdUserMatchCheckService.Run([item], [], computers, DisabledOu);

        // Computer now exists, but a row already parked in ComputerNotInAD is left there - the
        // caller (a human) needs to move it back on purpose.
        Assert.Equal("ComputerNotInAD", item.Status);
        Assert.Equal(0, result.AutoMovedComputerNotInAdCount);
        Assert.Equal("", item.ADComputerNotFound);
    }

    [Fact]
    public void DisabledComputer_ByUserAccountControl_FlagsAndAutoMovesToDisabledDevices()
    {
        var item = MakeItem(1, deviceName: "DEV-1");
        var computers = new[] { new AdComputerMatchInfo("DEV-1", "CN=DEV-1,OU=Domain Computers,DC=x", false) };
        var result = AdUserMatchCheckService.Run([item], [], computers, DisabledOu);

        Assert.Equal("Computer object disabled in AD", item.ADComputerDisabled);
        Assert.Equal("DisabledDevices", item.Status);
        Assert.Equal(1, result.ComputerDisabledCount);
        Assert.Equal(1, result.AutoMovedCount);
    }

    [Fact]
    public void DisabledComputer_ByOuLocation_FlagsAndAutoMoves_EvenIfEnabled()
    {
        var item = MakeItem(1, deviceName: "DEV-1");
        var computers = new[] { new AdComputerMatchInfo("DEV-1", $"CN=DEV-1,{DisabledOu}", true) };
        var result = AdUserMatchCheckService.Run([item], [], computers, DisabledOu);

        Assert.Equal("Computer object disabled in AD", item.ADComputerDisabled);
        Assert.Equal("DisabledDevices", item.Status);
    }

    [Fact]
    public void DisabledComputer_OffDomain_StillAutoMoved_UnlikeTheOtherTwoAxes()
    {
        var item = MakeItem(1, deviceName: "DEV-1", domain: "Off-Domain");
        var computers = new[] { new AdComputerMatchInfo("DEV-1", "CN=x", false) };
        var result = AdUserMatchCheckService.Run([item], [], computers, DisabledOu);

        // Matches the PS source exactly: only the ComputerNotInAD auto-move is off-domain-exempt,
        // not the Disabled Devices one.
        Assert.Equal("DisabledDevices", item.Status);
    }

    [Fact]
    public void AlreadyInDisabledDevices_NotReMovedAgain_ButFlagStillRefreshed()
    {
        var item = MakeItem(1, deviceName: "DEV-1", status: "DisabledDevices");
        var computers = new[] { new AdComputerMatchInfo("DEV-1", "CN=x", false) };
        var result = AdUserMatchCheckService.Run([item], [], computers, DisabledOu);

        Assert.Equal("DisabledDevices", item.Status);
        Assert.Equal(0, result.AutoMovedCount);
        Assert.Equal("Computer object disabled in AD", item.ADComputerDisabled);
    }

    [Fact]
    public void ComputerReEnabled_LeftInDisabledDevices_ForHumanToMoveBack()
    {
        var item = MakeItem(1, deviceName: "DEV-1", status: "DisabledDevices");
        var computers = new[] { new AdComputerMatchInfo("DEV-1", "CN=x", true) };
        AdUserMatchCheckService.Run([item], [], computers, DisabledOu);

        Assert.Equal("DisabledDevices", item.Status);
        Assert.Equal("", item.ADComputerDisabled);
    }


    [Fact]
    public void UnaffectedRow_ProducesNoChangeEntry()
    {
        // Francois, 2026-09-17: "log everything that was changed/moved" must not mean a line for
        // every row checked - a row nothing happened to should produce nothing. Names must already
        // match the AD user, or the self-heal itself would count as a real change (see the
        // NameSelfHeal test below).
        var item = MakeItem(1, username: "jsmith", firstName: "Jane", lastName: "Smith");
        var users = new[] { new AdUserMatchInfo("jsmith", "Jane", "Smith", true) };
        var computers = new[] { new AdComputerMatchInfo("DEV-1", "CN=DEV-1,OU=x", true) };
        var result = AdUserMatchCheckService.Run([item], users, computers, DisabledOu);
        Assert.Empty(result.Changes);
    }

    [Fact]
    public void ComputerDisabled_ProducesChangeEntry_DescribingTheMoveAndFlag()
    {
        var item = MakeItem(1, deviceName: "DEV-1", username: "jsmith", status: "Current");
        var users = new[] { new AdUserMatchInfo("jsmith", "Jane", "Smith", true) };
        var computers = new[] { new AdComputerMatchInfo("DEV-1", "CN=DEV-1," + DisabledOu, true) };
        var result = AdUserMatchCheckService.Run([item], users, computers, DisabledOu);

        var change = Assert.Single(result.Changes);
        Assert.Equal("DEV-1", change.DeviceName);
        Assert.Contains("moved from 'Current Inventory' to 'Disabled Devices'", change.Summary);
        Assert.Contains("computer disabled in AD", change.Summary);
    }

    [Fact]
    public void NameSelfHeal_ProducesChangeEntry_EvenWithNoMoveOrFlagChange()
    {
        var item = MakeItem(1, username: "jsmith", lastName: "Old", firstName: "Name");
        var users = new[] { new AdUserMatchInfo("jsmith", "Jane", "Smith", true) };
        var computers = new[] { new AdComputerMatchInfo("DEV-1", "CN=x", true) };
        var result = AdUserMatchCheckService.Run([item], users, computers, DisabledOu);

        var change = Assert.Single(result.Changes);
        Assert.Contains("name refreshed from AD: 'Name Old' -> 'Jane Smith'", change.Summary);
    }

    [Fact]
    public void IgnoreAdSyncRow_ProducesNoChangeEntry()
    {
        var item = MakeItem(1, deviceName: "GHOST-DEVICE", username: "ghost", status: "Current");
        item.IgnoreAdSync = "Yes";
        var result = AdUserMatchCheckService.Run([item], [], [], DisabledOu);
        Assert.Empty(result.Changes);
    }

    [Fact]
    public void IgnoreAdSyncRow_IsSkippedEntirely_NoFlagsAndNoAutoMove()
    {
        // A row flagged "don't touch me" (Francois, 2026-09-17) must come out of Run() completely
        // unchanged, even though every signal here would otherwise flag AND auto-move it (a ghost
        // device with a ghost username on Current Inventory).
        var item = MakeItem(1, deviceName: "GHOST-DEVICE", username: "ghost", status: "Current");
        item.IgnoreAdSync = "Yes";
        var result = AdUserMatchCheckService.Run([item], [], [], DisabledOu);

        Assert.Equal("Current", item.Status);
        Assert.Equal("", item.ADFlagReason);
        Assert.Equal("", item.ADComputerNotFound);
        Assert.Equal("", item.ADComputerDisabled);
        Assert.Equal(0, result.ComputerNotFoundCount);
        Assert.Equal(0, result.NotFoundDeviceCount);
        Assert.Equal(0, result.AutoMovedComputerNotInAdCount);
    }

    [Fact]
    public void BlankDeviceName_NeverCountsAsComputerNotFound()
    {
        var item = MakeItem(1, deviceName: "");
        var result = AdUserMatchCheckService.Run([item], [], [], DisabledOu);
        Assert.Equal("", item.ADComputerNotFound);
        Assert.Equal(0, result.ComputerNotFoundCount);
    }

    [Fact]
    public void TotalAdUsers_ReflectsFullUserListCount()
    {
        var users = new[]
        {
            new AdUserMatchInfo("a", "A", "One", true),
            new AdUserMatchInfo("b", "B", "Two", true),
        };
        var result = AdUserMatchCheckService.Run([], users, [], DisabledOu);
        Assert.Equal(2, result.TotalAdUsers);
    }
}
