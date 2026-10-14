using InventoryManager.Core.Models;

namespace InventoryManager.Core.Ad;

/// <summary>One enabled AD user with no matching inventory record.</summary>
public sealed record AdUserNoDevice(string Username, string FirstName, string LastName);

/// <summary>
/// Direct port of the PowerShell build's Get-AdUsersWithNoDevice - Francois, 2026-09-25: "I want to
/// scan AD and import all of the users who are in AD and are currently not being matched to a
/// device." A live AD report, not a view over any Status - unlike every other tab in this app, a
/// row here doesn't correspond to an existing InventoryItem at all.
/// <para>A user counts as "has a device" if any inventory row is anchored to them by Username (the
/// authoritative Match User anchor), or - for rows never run through Match User yet - by a
/// Last+First Name match. <b>ITStock</b> and <b>DisabledDevices</b> rows don't count: IT Stock is
/// explicitly unassigned bench stock, and Disabled Devices (the 2026-09-23 successor to the removed
/// Terminated tab - see the tab-removal work) is where a device lands once its person has left, so
/// its name belongs to whoever HAD the device, not a current assignment.</para>
/// <para>AD users with neither GivenName nor Surname set are skipped - almost always a service/
/// system account rather than a real person who'd need a device tracked in inventory. Only enabled
/// AD accounts are considered - a disabled account isn't someone who currently needs a device.
/// </para>
/// </summary>
public static class UsersWithNoDeviceFinder
{
    private static readonly HashSet<string> ExcludedFromHasDevice =
        new(StringComparer.OrdinalIgnoreCase) { "ITStock", "DisabledDevices" };

    public static IReadOnlyList<AdUserNoDevice> Find(IReadOnlyList<InventoryItem> items, IReadOnlyList<AdUserMatchInfo> allAdUsers)
    {
        var usernamesWithDevice = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var namesWithDevice = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (ExcludedFromHasDevice.Contains(item.Status))
            {
                continue;
            }
            var username = item.Username.Trim();
            if (!string.IsNullOrWhiteSpace(username))
            {
                usernamesWithDevice.Add(username);
            }
            var lastName = item.LastName.Trim();
            var firstName = item.FirstName.Trim();
            if (!string.IsNullOrWhiteSpace(lastName) && !string.IsNullOrWhiteSpace(firstName))
            {
                namesWithDevice.Add($"{lastName}|{firstName}");
            }
        }

        var result = new List<AdUserNoDevice>();
        foreach (var user in allAdUsers)
        {
            if (!user.Enabled)
            {
                continue;
            }
            if (string.IsNullOrWhiteSpace(user.GivenName) && string.IsNullOrWhiteSpace(user.Surname))
            {
                continue;
            }
            if (usernamesWithDevice.Contains(user.SamAccountName.Trim()))
            {
                continue;
            }
            var key = $"{user.Surname.Trim()}|{user.GivenName.Trim()}";
            if (namesWithDevice.Contains(key))
            {
                continue;
            }
            result.Add(new AdUserNoDevice(user.SamAccountName, user.GivenName, user.Surname));
        }

        return result
            .OrderBy(u => u.LastName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(u => u.FirstName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
