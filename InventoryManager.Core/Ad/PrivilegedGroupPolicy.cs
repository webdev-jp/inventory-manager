namespace InventoryManager.Core.Ad;

/// <summary>
/// Direct port of the PowerShell build's privileged-group deny list
/// ($script:PrivilegedGroupRids/PrivilegedBuiltinRids/PrivilegedGroupNames,
/// Get-PrivilegedGroupSids, Test-GroupIsPrivileged). Defense-in-depth only - the real enforcement
/// boundary is AD delegation on whatever account the SU login resolves to; this just stops an
/// authorized operator from adding someone to Domain Admins etc. by mistake through this app's
/// own onboarding UI.
/// </summary>
public static class PrivilegedGroupPolicy
{
    // SID-based (primary defense): matched by well-known RID, so renaming a group in AD does not
    // bypass this - only the RID does. BUILTIN\* groups use the fixed S-1-5-32-<rid> form
    // (identical across every domain); the others are this domain's own SID + RID.

    /// <summary>Domain/Schema/Enterprise Admins, Group Policy Creator Owners, Key/Enterprise Key Admins.</summary>
    public static readonly IReadOnlyList<int> PrivilegedGroupRids = [512, 518, 519, 520, 526, 527];

    /// <summary>Administrators, Account/Server/Print/Backup Operators.</summary>
    public static readonly IReadOnlyList<int> PrivilegedBuiltinRids = [544, 548, 549, 550, 551];

    /// <summary>Name-based (supplemental): a small number of highly-privileged groups do NOT
    /// have a fixed well-known RID - their SID is allocated normally when the relevant role
    /// (DNS, AD CS, etc.) is installed, so they can only be matched by name.</summary>
    public static readonly IReadOnlyList<string> PrivilegedGroupNames =
    [
        "DnsAdmins",                    // full DC code execution via a malicious DLL plugin
        "Cert Publishers",
        "Organization Management",      // Exchange - full mailbox/role control, common DA pivot
        "Exchange Trusted Subsystem",   // Exchange - writes to AD as SYSTEM on Exchange servers
        "Exchange Windows Permissions", // Exchange - can grant DCSync-capable rights
        "Enterprise Read-only Domain Controllers",
    ];

    public static HashSet<string> GetPrivilegedGroupSids(string? domainSid)
    {
        var sids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rid in PrivilegedBuiltinRids)
        {
            sids.Add($"S-1-5-32-{rid}");
        }
        if (!string.IsNullOrWhiteSpace(domainSid))
        {
            foreach (var rid in PrivilegedGroupRids)
            {
                sids.Add($"{domainSid}-{rid}");
            }
        }
        return sids;
    }

    /// <summary>
    /// Name check first (cheap, catches the RID-less groups like DnsAdmins); SID check second
    /// (catches every well-known-RID group even if it's been renamed in AD). Fails CLOSED (true)
    /// if the group's SID can't be resolved at all, since this is used right before a real AD
    /// write - an unresolvable group is never assumed safe.
    /// </summary>
    public static bool IsPrivileged(string groupName, string? groupSid, string? domainSid)
    {
        if (PrivilegedGroupNames.Contains(groupName, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }
        if (string.IsNullOrWhiteSpace(groupSid))
        {
            return true;
        }
        return GetPrivilegedGroupSids(domainSid).Contains(groupSid);
    }
}
