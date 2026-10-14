using InventoryManager.Core.Ad;
using InventoryManager.Core.Models;
using InventoryManager.Core.Transfer;

namespace InventoryManager.Core.Tests;

public class ComputerIdentityHandoffTests
{
    private const string DisabledOu = "OU=Disabled Computers,OU=Domain Computers,DC=contoso,DC=local";

    [Fact]
    public void Apply_NewDeviceWasDisabled_EnablesNewAndDisablesOldAndRelocates()
    {
        var ad = new FakeAdService();
        ad.Computers["OLD-PC"] = new AdComputerInfo("OLD-PC", "CN=OLD-PC,OU=Domain Computers,DC=contoso,DC=local", true, "Jane Doe***");
        ad.Computers["NEW-PC"] = new AdComputerInfo("NEW-PC", "CN=NEW-PC,DC=x", false, "");

        var oldItem = new InventoryItem { Id = 1, DeviceName = "OLD-PC", Status = "Current", FirstName = "Jane", LastName = "Doe", Username = "doej", Description = "Jane Doe***", ADFlagReason = "Flagged reason" };
        oldItem["CellPhone"] = "914-555-0100";
        oldItem["DeviceModel"] = "iPhone 15";
        var newItem = new InventoryItem { Id = 2, DeviceName = "NEW-PC", Status = "ITStock" };

        ComputerIdentityHandoff.Apply(ad, oldItem, newItem, "Jane", "Doe", "doej", "Flagged reason", DisabledOu);

        // New device needed enabling (Francois, 2026-09-18) -> old device disabled in AD, moved
        // into the Disabled Computers OU, Description cleared, local identity wiped, moved to
        // Disabled Devices.
        Assert.False(ad.Computers["OLD-PC"].Enabled);
        Assert.EndsWith(DisabledOu, ad.Computers["OLD-PC"].DistinguishedName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("", ad.Computers["OLD-PC"].Description);
        Assert.Equal("", oldItem.Username);
        Assert.Equal("", oldItem.FirstName);
        Assert.Equal("", oldItem.LastName);
        Assert.Equal("", oldItem.Description);
        Assert.Equal("", oldItem["CellPhone"]);
        Assert.Equal("", oldItem["DeviceModel"]);
        Assert.Equal("", oldItem.ADFlagReason);
        Assert.Equal("DisabledDevices", oldItem.Status);
        Assert.Equal("Computer object disabled in AD", oldItem.ADComputerDisabled);

        // New device: enabled, takes over identity, AD standing, and the person's own cellphone
        // info (this belongs to the person, not the laptop - Francois, 2026-09-17) - Status Current.
        Assert.True(ad.Computers["NEW-PC"].Enabled);
        Assert.Equal("Jane Doe***", ad.Computers["NEW-PC"].Description);
        Assert.Equal("doej", newItem.Username);
        Assert.Equal("Jane", newItem.FirstName);
        Assert.Equal("Doe", newItem.LastName);
        Assert.Equal("Jane Doe***", newItem.Description);
        Assert.Equal("914-555-0100", newItem["CellPhone"]);
        Assert.Equal("iPhone 15", newItem["DeviceModel"]);
        Assert.Equal("Flagged reason", newItem.ADFlagReason); // carried over from the old device
        Assert.Equal("Current", newItem.Status);
        Assert.Equal("", newItem.ADComputerDisabled);
    }

    [Fact]
    public void Apply_NewDeviceAlreadyEnabled_LeavesBothDevicesEnabledStateAlone()
    {
        // Francois, 2026-09-18: "checking whether the new device is enabled or not, if its enabled
        // then leave it alone and do nothing" - the whole enable/disable/OU-relocate/Disabled-
        // Devices-flag step is skipped entirely when the new device doesn't need enabling. The
        // person's identity/personal fields still move away from the old device regardless (that
        // part is unconditional - the person is leaving it either way).
        var ad = new FakeAdService();
        ad.Computers["OLD-PC"] = new AdComputerInfo("OLD-PC", "CN=OLD-PC,OU=Domain Computers,DC=contoso,DC=local", true, "Jane Doe***");
        ad.Computers["NEW-PC"] = new AdComputerInfo("NEW-PC", "CN=NEW-PC,DC=x", true, "");

        var oldItem = new InventoryItem { Id = 1, DeviceName = "OLD-PC", Status = "Current", FirstName = "Jane", LastName = "Doe", Username = "doej", Description = "Jane Doe***" };
        oldItem["CellPhone"] = "914-555-0100";
        var newItem = new InventoryItem { Id = 2, DeviceName = "NEW-PC", Status = "ITStock" };

        ComputerIdentityHandoff.Apply(ad, oldItem, newItem, "Jane", "Doe", "doej", "", DisabledOu);

        // Old device: left enabled, NOT relocated to Disabled Computers, NOT flagged/moved to
        // Disabled Devices locally - but its identity/personal fields are still cleared, since the
        // person is leaving it regardless of its own AD enabled state.
        Assert.True(ad.Computers["OLD-PC"].Enabled);
        Assert.Empty(ad.MoveComputerCalls);
        Assert.Equal("Current", oldItem.Status);
        Assert.Equal("", oldItem.ADComputerDisabled);
        Assert.Equal("", oldItem.Username);
        Assert.Equal("", oldItem.Description);
        Assert.Equal("", oldItem["CellPhone"]);

        // New device was already fine - untouched enabled-wise, but still takes over the identity.
        Assert.True(ad.Computers["NEW-PC"].Enabled);
        Assert.Equal("doej", newItem.Username);
        Assert.Equal("914-555-0100", newItem["CellPhone"]);
        Assert.Equal("Current", newItem.Status);
    }

    [Fact]
    public void Apply_OldDeviceAlreadyInDisabledOu_NotMovedAgain()
    {
        // Francois, 2026-09-17: mirrors the same "already there -> leave it alone" guard the
        // Move-to dropdown's own disable-on-move helper uses - no redundant AD write. New device
        // must be seeded disabled here so the enable/disable/relocate branch actually runs at all.
        var ad = new FakeAdService();
        ad.Computers["OLD-PC"] = new AdComputerInfo("OLD-PC", $"CN=OLD-PC,{DisabledOu}", true, "");
        ad.Computers["NEW-PC"] = new AdComputerInfo("NEW-PC", "CN=NEW-PC,DC=x", false, "");

        var oldItem = new InventoryItem { Id = 1, DeviceName = "OLD-PC" };
        var newItem = new InventoryItem { Id = 2, DeviceName = "NEW-PC" };

        ComputerIdentityHandoff.Apply(ad, oldItem, newItem, "Jane", "Doe", "doej", "", DisabledOu);

        Assert.Empty(ad.MoveComputerCalls);
        Assert.EndsWith(DisabledOu, ad.Computers["OLD-PC"].DistinguishedName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Apply_NewDeviceHadItsOwnStaleCellphoneInfo_OverwrittenByOldDevices()
    {
        // The new device is presumably coming from IT Stock/Unknown/etc. and might carry stale
        // cellphone fields from whoever had it last - the incoming person's own info must win, not
        // merge with or defer to whatever was already sitting there.
        var ad = new FakeAdService();
        ad.Computers["OLD-PC"] = new AdComputerInfo("OLD-PC", "CN=OLD-PC,DC=x", true, "");
        ad.Computers["NEW-PC"] = new AdComputerInfo("NEW-PC", "CN=NEW-PC,DC=x", true, "");

        var oldItem = new InventoryItem { Id = 1, DeviceName = "OLD-PC" };
        oldItem["CellPhone"] = "914-555-0100";
        oldItem["DeviceModel"] = "iPhone 15";
        var newItem = new InventoryItem { Id = 2, DeviceName = "NEW-PC" };
        newItem["CellPhone"] = "555-000-9999";
        newItem["DeviceModel"] = "Stale Old Phone";

        ComputerIdentityHandoff.Apply(ad, oldItem, newItem, "Jane", "Doe", "doej", "", DisabledOu);

        Assert.Equal("914-555-0100", newItem["CellPhone"]);
        Assert.Equal("iPhone 15", newItem["DeviceModel"]);
    }

    [Fact]
    public void Apply_DoesNotTouchHardwareFields_OnlyIdentityFields()
    {
        var ad = new FakeAdService();
        ad.Computers["OLD-PC"] = new AdComputerInfo("OLD-PC", "CN=OLD-PC,DC=x", true, "");
        ad.Computers["NEW-PC"] = new AdComputerInfo("NEW-PC", "CN=NEW-PC,DC=x", true, "");

        var oldItem = new InventoryItem { Id = 1, DeviceName = "OLD-PC" };
        var newItem = new InventoryItem { Id = 2, DeviceName = "NEW-PC" };
        newItem["Model"] = "Latitude 5420";
        newItem["SerialNumber"] = "ABC123";

        ComputerIdentityHandoff.Apply(ad, oldItem, newItem, "Jane", "Doe", "doej", "", DisabledOu);

        Assert.Equal("Latitude 5420", newItem["Model"]);
        Assert.Equal("ABC123", newItem["SerialNumber"]);
    }

    [Fact]
    public void Apply_AdCallFails_StillUpdatesLocalFields()
    {
        // Neither computer is registered in the fake AD - SetComputerEnabled/SetComputerDescription/
        // GetComputer/MoveComputer will all throw or return null for both. A computer that can't be
        // found/queried at all is treated the same as "needs enabling" (the old, safer default) -
        // local field updates must still happen regardless - AD sync failures never block the
        // primary local change, same rule as everywhere else.
        var ad = new FakeAdService();
        var oldItem = new InventoryItem { Id = 1, DeviceName = "OLD-PC", Username = "doej" };
        var newItem = new InventoryItem { Id = 2, DeviceName = "NEW-PC" };

        ComputerIdentityHandoff.Apply(ad, oldItem, newItem, "Jane", "Doe", "doej", "", DisabledOu);

        Assert.Equal("DisabledDevices", oldItem.Status);
        Assert.Equal("Current", newItem.Status);
        Assert.Equal("doej", newItem.Username);
    }
}
