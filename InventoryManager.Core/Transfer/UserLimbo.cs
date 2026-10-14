using InventoryManager.Core.Models;

namespace InventoryManager.Core.Transfer;

public sealed record LimboResult(InventoryItem LimboRow, bool Created);

/// <summary>
/// "Limbo" - a person who still exists but whose device has been taken away. Francois, 2026-10-14:
/// disabling a device should not disable (or lose track of) the user attached to it; if the user
/// isn't being given a new device right away they go into Limbo. Limbo is represented the way the
/// app already represents a user with no computer: a Current Inventory row whose Device Name is the
/// "No PC" placeholder (<see cref="InventorySchema.NoDevicePlaceholder"/>), so every existing
/// feature (AD Sync, Users with no Devices, CSV import) already understands it.
/// <para>Pure inventory-side logic - this never touches Active Directory. The caller disables the
/// computer; this only splits the person off the device row.</para>
/// </summary>
public static class UserLimbo
{
    /// <summary>True when the row has anyone attached to it (a linked Username, or just a name).</summary>
    public static bool HasAssignedUser(InventoryItem item) =>
        !string.IsNullOrWhiteSpace(item.Username)
        || !string.IsNullOrWhiteSpace(item.FirstName)
        || !string.IsNullOrWhiteSpace(item.LastName);

    /// <summary>
    /// Moves the person off <paramref name="deviceRow"/> into a "No PC" row (reusing one that already
    /// exists for them, adding a new one otherwise) and clears the person's details from the device
    /// row. What belongs to the PERSON - name, Username, Cellphone # and Cellphone Model, the same set
    /// Transfer User moves - goes with them; the device keeps its hardware fields. The device row's
    /// Description is cleared too (it described the assignee), exactly as a transfer does.
    /// </summary>
    public static LimboResult MoveUserToLimbo(List<InventoryItem> items, InventoryItem deviceRow)
    {
        if (InventorySchema.IsNoDevice(deviceRow.DeviceName))
        {
            throw new ArgumentException("This row is already a no-device row - the user is already in Limbo.", nameof(deviceRow));
        }
        if (!HasAssignedUser(deviceRow))
        {
            throw new ArgumentException("This device has no user attached.", nameof(deviceRow));
        }

        var username = deviceRow.Username.Trim();
        var firstName = deviceRow.FirstName.Trim();
        var lastName = deviceRow.LastName.Trim();
        var cellPhone = deviceRow["CellPhone"];
        var cellPhoneModel = deviceRow["DeviceModel"];

        var existing = items.FirstOrDefault(i =>
            !ReferenceEquals(i, deviceRow)
            && InventorySchema.IsNoDevice(i.DeviceName)
            && SamePerson(i, username, firstName, lastName));

        InventoryItem limbo;
        bool created;
        if (existing is not null)
        {
            limbo = existing;
            created = false;
            if (string.IsNullOrWhiteSpace(limbo["CellPhone"]))
            {
                limbo["CellPhone"] = cellPhone;
            }
            if (string.IsNullOrWhiteSpace(limbo["DeviceModel"]))
            {
                limbo["DeviceModel"] = cellPhoneModel;
            }
        }
        else
        {
            limbo = new InventoryItem
            {
                Id = InventorySchema.GetNextId(items),
                Status = "Current",
                DeviceName = InventorySchema.NoDevicePlaceholder,
                Username = username,
                FirstName = firstName,
                LastName = lastName,
            };
            limbo["CellPhone"] = cellPhone;
            limbo["DeviceModel"] = cellPhoneModel;
            items.Add(limbo);
            created = true;
        }

        deviceRow.Username = "";
        deviceRow.FirstName = "";
        deviceRow.LastName = "";
        deviceRow.Description = "";
        deviceRow["CellPhone"] = "";
        deviceRow["DeviceModel"] = "";
        deviceRow.ADFlagReason = "";
        return new LimboResult(limbo, created);
    }

    private static bool SamePerson(InventoryItem row, string username, string firstName, string lastName)
    {
        if (username.Length > 0 && row.Username.Trim().Length > 0)
        {
            return string.Equals(row.Username.Trim(), username, StringComparison.OrdinalIgnoreCase);
        }
        return lastName.Length > 0
            && string.Equals(row.LastName.Trim(), lastName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(row.FirstName.Trim(), firstName, StringComparison.OrdinalIgnoreCase);
    }
}
