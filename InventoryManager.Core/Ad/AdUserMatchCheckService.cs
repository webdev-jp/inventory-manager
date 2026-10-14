using InventoryManager.Core.Models;
using InventoryManager.Core;

namespace InventoryManager.Core.Ad;

public sealed record AdMatchCheckResult(
    int TotalAdUsers,
    int DisabledDeviceCount,
    int NotFoundDeviceCount,
    int ComputerNotFoundCount,
    int AutoMovedCount,
    int AutoMovedComputerNotInAdCount,
    int ComputerDisabledCount,
    IReadOnlyList<AdSyncChangeEntry> Changes);

/// <summary>One row this run actually changed something on - device name plus a human-readable
/// summary of every fact that changed (move, flag, or name refresh), joined into one line per
/// device rather than one entry per fact, since this feeds the Activity Log (App layer) rather
/// than the tamper-evident per-field ledger (which already separately records Status/FirstName/
/// LastName changes - see InventoryStore.ComputeFieldChanges). A row nothing changed about never
/// produces an entry - Francois asked (2026-09-17) to see "everything that was changed/moved",
/// not a line for every single row checked.</summary>
public sealed record AdSyncChangeEntry(string DeviceName, string Summary);

/// <summary>
/// Direct port of the PowerShell build's Invoke-AdUserMatchCheck: classifies every inventory row
/// against AD on two independent axes (the computer axis wins ties, since a computer that isn't in
/// AD at all is a stronger signal than a name that doesn't resolve):
///   1. The COMPUTER itself (by Device Name) - does an AD computer object with this name exist at
///      all? If not, auto-moved to Computer Not in AD. Separately, a computer that DOES exist but
///      is disabled or parked in the Disabled Computers OU gets ADComputerDisabled stamped and is
///      auto-moved to Disabled Devices - that always takes final priority over the "not found"
///      classification if somehow both applied.
///   2. The assigned USER (by the anchored Username from Match User, self-healingly pulling
///      current First/Last Name from AD every run so a real name change doesn't break the match;
///      or by Last+First Name for rows with no anchored Username): matched+enabled (flag
///      cleared), matched+disabled ("AD account disabled"), or no matching AD user at all ("No
///      matching AD user found" - flagged/highlighted only, no longer auto-moved anywhere; the
///      "User Not in AD" tab was removed 2026-09-23, and this is the sole surviving signal for it,
///      same as the AD Sync dialog's own "User Not Found in AD" report tab which already read this
///      flag directly rather than the tab).
/// Off-Domain devices are excluded from the Computer Not in AD auto-move (being off-domain is
/// itself a plausible reason a device wouldn't resolve against this domain's AD) - they are NOT
/// excluded from the Disabled Devices auto-move, matching the PS source exactly.
/// Rows already in Computer Not in AD or Disabled Devices are left alone for the Computer Not in AD
/// auto-move (that's the "final" classification for that axis).
/// A row with IgnoreAdSync="Yes" is skipped entirely, before any classification runs - see the
/// per-row check inside the loop below.
/// <see cref="AdMatchCheckResult.Changes"/> lists every row that actually changed something (a
/// move, a flag change, or a self-healed name refresh), one human-readable summary line per
/// device - added 2026-09-17 so the caller can log exactly what happened, not just aggregate
/// counts. A row nothing changed about produces no entry.
/// Caller is responsible for saving/refreshing afterward - this only mutates the in-memory items.
/// </summary>
public static class AdUserMatchCheckService
{
    public static AdMatchCheckResult Run(
        IReadOnlyList<InventoryItem> items,
        IReadOnlyList<AdUserMatchInfo> allUsers,
        IReadOnlyList<AdComputerMatchInfo> allComputers,
        string disabledComputersOuDn)
    {
        var enabledKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usersBySam = new Dictionary<string, AdUserMatchInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var user in allUsers)
        {
            if (!string.IsNullOrWhiteSpace(user.SamAccountName))
            {
                usersBySam[user.SamAccountName] = user;
            }
            if (string.IsNullOrWhiteSpace(user.Surname) || string.IsNullOrWhiteSpace(user.GivenName))
            {
                continue;
            }
            var key = $"{user.Surname.Trim()}|{user.GivenName.Trim()}";
            allKeys.Add(key);
            if (user.Enabled)
            {
                enabledKeys.Add(key);
            }
        }

        var disabledOuSuffix = ("," + disabledComputersOuDn).ToLowerInvariant();
        var disabledComputerNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allComputerNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var computer in allComputers)
        {
            var name = computer.Name.Trim().ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(name))
            {
                allComputerNames.Add(name);
            }
            var inDisabledOu = computer.DistinguishedName.ToLowerInvariant().EndsWith(disabledOuSuffix, StringComparison.Ordinal);
            if (inDisabledOu || !computer.Enabled)
            {
                disabledComputerNames.Add(name);
            }
        }

        var disabledCount = 0;
        var notFoundCount = 0;
        var computerNotFoundCount = 0;
        var computerDisabledCount = 0;
        var autoMovedCount = 0;
        var autoMovedComputerNotInAdCount = 0;
        var changes = new List<AdSyncChangeEntry>();

        foreach (var row in items)
        {
            // Explicit per-row opt-out (Francois, 2026-09-17): a known-good exception this check
            // would otherwise flag/relocate - e.g. a shared/kiosk device with no real assigned
            // user, or a computer intentionally left disabled while still in active use. Skipped
            // BEFORE any of this row's flags/status are touched, so it stays exactly as the
            // operator left it no matter what AD currently shows. The explicit per-row "Check AD"
            // context-menu action is a separate code path that does not call this method, so it is
            // intentionally unaffected by this flag.
            if (row.IgnoreAdSync == "Yes")
            {
                continue;
            }

            var beforeStatus = row.Status;
            var beforeFlag = row.ADFlagReason;
            var beforeComputerNotFound = row.ADComputerNotFound;
            var beforeComputerDisabled = row.ADComputerDisabled;
            var beforeFirstName = row.FirstName;
            var beforeLastName = row.LastName;

            var username = row.Username.Trim();
            if (!string.IsNullOrWhiteSpace(username) && usersBySam.TryGetValue(username, out var adUser))
            {
                row.FirstName = adUser.GivenName;
                row.LastName = adUser.Surname;
                if (adUser.Enabled)
                {
                    row.ADFlagReason = "";
                }
                else
                {
                    row.ADFlagReason = "AD account disabled";
                    disabledCount++;
                }
            }
            else if (!string.IsNullOrWhiteSpace(username))
            {
                row.ADFlagReason = "No matching AD user found";
                notFoundCount++;
            }
            else
            {
                var key = $"{row.LastName.Trim()}|{row.FirstName.Trim()}";
                if (key == "|")
                {
                    row.ADFlagReason = "";
                }
                else if (enabledKeys.Contains(key))
                {
                    row.ADFlagReason = "";
                }
                else if (allKeys.Contains(key))
                {
                    row.ADFlagReason = "AD account disabled";
                    disabledCount++;
                }
                else
                {
                    row.ADFlagReason = "No matching AD user found";
                    notFoundCount++;
                }
            }

            // The "No PC" placeholder names no computer, so it's treated exactly like a blank
            // Device Name - never "computer not found" (see InventorySchema.NoDevicePlaceholder).
            var deviceKey = InventorySchema.IsNoDevice(row.DeviceName) ? "" : row.DeviceName.Trim().ToLowerInvariant();
            var isOffDomain = row["Domain"].Trim() == "Off-Domain";
            var computerExists = string.IsNullOrWhiteSpace(deviceKey) || allComputerNames.Contains(deviceKey);
            row.ADComputerNotFound = computerExists ? "" : "No matching AD computer object found";
            if (!computerExists)
            {
                computerNotFoundCount++;
            }

            if (!isOffDomain && !computerExists && row.Status is not ("ComputerNotInAD" or "DisabledDevices"))
            {
                row.Status = "ComputerNotInAD";
                autoMovedComputerNotInAdCount++;
            }

            if (!string.IsNullOrWhiteSpace(deviceKey) && disabledComputerNames.Contains(deviceKey))
            {
                row.ADComputerDisabled = "Computer object disabled in AD";
                computerDisabledCount++;
                if (row.Status != "DisabledDevices")
                {
                    row.Status = "DisabledDevices";
                    autoMovedCount++;
                }
            }
            else
            {
                row.ADComputerDisabled = "";
            }

            var facts = new List<string>();
            if (beforeStatus != row.Status)
            {
                var fromLabel = InventorySchema.Statuses.FirstOrDefault(s => s.Key == beforeStatus)?.Label ?? beforeStatus;
                var toLabel = InventorySchema.Statuses.FirstOrDefault(s => s.Key == row.Status)?.Label ?? row.Status;
                facts.Add($"moved from '{fromLabel}' to '{toLabel}'");
            }
            if (beforeFlag != row.ADFlagReason)
            {
                facts.Add($"AD flag changed from '{(beforeFlag.Length == 0 ? "(none)" : beforeFlag)}' to '{(row.ADFlagReason.Length == 0 ? "(none)" : row.ADFlagReason)}'");
            }
            if (beforeComputerNotFound != row.ADComputerNotFound)
            {
                facts.Add(row.ADComputerNotFound.Length == 0 ? "computer found in AD (flag cleared)" : "computer not found in AD");
            }
            if (beforeComputerDisabled != row.ADComputerDisabled)
            {
                facts.Add(row.ADComputerDisabled.Length == 0 ? "computer no longer disabled in AD (flag cleared)" : "computer disabled in AD");
            }
            if (beforeFirstName != row.FirstName || beforeLastName != row.LastName)
            {
                var beforeName = $"{beforeFirstName} {beforeLastName}".Trim();
                var afterName = $"{row.FirstName} {row.LastName}".Trim();
                facts.Add($"name refreshed from AD: '{beforeName}' -> '{afterName}'");
            }
            if (facts.Count > 0)
            {
                changes.Add(new AdSyncChangeEntry(row.DeviceName, string.Join("; ", facts) + "."));
            }
        }

        return new AdMatchCheckResult(
            allUsers.Count, disabledCount, notFoundCount, computerNotFoundCount,
            autoMovedCount, autoMovedComputerNotInAdCount, computerDisabledCount,
            changes);
    }
}
