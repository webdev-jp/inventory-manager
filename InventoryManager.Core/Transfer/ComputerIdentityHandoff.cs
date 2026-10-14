using InventoryManager.Core.Ad;
using InventoryManager.Core.Models;

namespace InventoryManager.Core.Transfer;

/// <summary>
/// Direct port of the PowerShell build's Invoke-ComputerIdentityHandoff: shared by both the old
/// computer-only Transfer and today's general Transfer User, so the actual AD-write/local-field
/// logic for moving a person's identity between two devices lives in exactly one place.
///
/// Old device: its Description cleared and its local identity fields (Username/Description/
/// FirstName/LastName/Cellphone #/Cellphone Model - these belong to the PERSON, not the laptop,
/// so they always move away regardless of anything below) wiped, unconditionally - the person is
/// leaving this device no matter what.
///
/// Enable/disable is a SEPARATE, conditional step (Francois, 2026-09-18): first checks whether the
/// NEW device is already enabled in AD. If it is, this step does nothing at all - neither device's
/// enabled state, OU placement, or local "disabled" flag/tab is touched, since nothing here needs
/// fixing. If the new device is disabled (or not found/unreachable - treated the same, erring
/// toward the old, safer unconditional-disable behavior rather than silently leaving a device that
/// should be disabled untouched), the new device gets enabled, and the old device gets disabled,
/// moved into the Disabled Computers OU (mirrors the same "leaving active service always ends up
/// disabled and relocated" rule the Move-to dropdown and Offboard User already apply), and marked
/// Disabled Devices locally.
///
/// New device: always takes over the identity, AD Description, and the person's own personal
/// fields - not the old device's hardware fields (Model/Serial/etc. stay whatever this device
/// actually is, since those describe the laptop itself, not whoever's using it).
/// </summary>
public static class ComputerIdentityHandoff
{
    public static void Apply(IAdService adService, InventoryItem oldItem, InventoryItem newItem, string firstName, string lastName, string username, string oldAdFlagReason, string disabledComputersOuDn)
    {
        var oldDeviceName = oldItem.DeviceName;
        var newDeviceName = newItem.DeviceName;
        var fullName = $"{firstName} {lastName}".Trim();
        var cellPhone = oldItem["CellPhone"];
        var cellPhoneModel = oldItem["DeviceModel"];

        bool newDeviceNeedsEnabling;
        try
        {
            var newComputer = adService.GetComputer(newDeviceName);
            newDeviceNeedsEnabling = newComputer is null || !newComputer.Enabled;
        }
        catch
        {
            newDeviceNeedsEnabling = true;
        }

        if (newDeviceNeedsEnabling)
        {
            try
            {
                adService.SetComputerEnabled(newDeviceName, true);
            }
            catch
            {
                // Saved locally regardless - AD sync failures never block the primary local change.
            }
            try
            {
                adService.SetComputerEnabled(oldDeviceName, false);
            }
            catch
            {
            }
            try
            {
                var oldComputer = adService.GetComputer(oldDeviceName);
                var disabledOuSuffix = ("," + disabledComputersOuDn).ToLowerInvariant();
                if (oldComputer is not null && !oldComputer.DistinguishedName.ToLowerInvariant().EndsWith(disabledOuSuffix))
                {
                    adService.MoveComputer(oldDeviceName, disabledComputersOuDn);
                }
            }
            catch
            {
            }
            oldItem.ADComputerDisabled = "Computer object disabled in AD";
            oldItem.Status = "DisabledDevices";
        }

        try
        {
            adService.SetComputerDescription(oldDeviceName, null);
        }
        catch
        {
        }
        oldItem.Description = "";
        oldItem.Username = "";
        oldItem.FirstName = "";
        oldItem.LastName = "";
        oldItem["CellPhone"] = "";
        oldItem["DeviceModel"] = "";
        oldItem.ADFlagReason = "";

        var newDescription = $"{fullName}***";
        try
        {
            adService.SetComputerDescription(newDeviceName, newDescription);
        }
        catch
        {
        }
        newItem.Username = username;
        newItem.FirstName = firstName;
        newItem.LastName = lastName;
        newItem.Description = newDescription;
        newItem["CellPhone"] = cellPhone;
        newItem["DeviceModel"] = cellPhoneModel;
        newItem.ADFlagReason = oldAdFlagReason;
        newItem.ADComputerDisabled = "";
        newItem.Status = "Current";
    }
}
