namespace InventoryManager.Core.Ad;

/// <summary>
/// Administrative accounts this app must never write to as a side effect, and must refuse to run
/// Transfer / Offboarding / Copy Permissions against. Francois, 2026-10-01, after the 9/30
/// incident (a "Users with no Devices" import pulled in every service/admin account as a "No PC"
/// row, AD Sync moved them all, and the audit mirror wrote to each account's AD Notes - which made
/// Entra Connect re-export teamsadmin/SPAdmin and fail with InvalidHardMatch): "completely exclude
/// any administrative accounts from being touched unless it was designed to be touched by the app."
/// <para>An account is protected if EITHER:</para>
/// <list type="bullet">
/// <item>it's a member (direct, nested, or primary group) of any group
/// <see cref="PrivilegedGroupPolicy"/> treats as privileged - Domain/Enterprise/Schema Admins,
/// Administrators, the Operators groups, DnsAdmins, etc.; or</item>
/// <item>its name is admin-style: contains "admin" (Administrator, OrgAdmin, teamsadmin, SPAdmin),
/// or is an SU account - "su" + the name of another existing account (sujdoe ->
/// jdoe). The "other account exists" half is what keeps ordinary staff whose names just
/// happen to start with "su" (SuttonV, SummersA) from matching.</item>
/// </list>
/// Fails closed: if AD can't be queried, <see cref="FailClosed"/> treats every account as
/// protected, so an AD hiccup can only ever skip a write, never let one through.
/// </summary>
public sealed class ProtectedAccountPolicy
{
    private readonly HashSet<string> _privileged;
    private readonly HashSet<string> _allSamAccountNames;
    private readonly string? _failClosedReason;

    private ProtectedAccountPolicy(HashSet<string> privileged, HashSet<string> allSamAccountNames, string? failClosedReason)
    {
        _privileged = privileged;
        _allSamAccountNames = allSamAccountNames;
        _failClosedReason = failClosedReason;
    }

    /// <summary>Queries AD for the privileged-member set and the full account list. Throws if AD
    /// can't be read - callers should catch and use <see cref="FailClosed"/>.</summary>
    public static ProtectedAccountPolicy Load(IAdService adService, string? domainSid) =>
        Create(adService.ListPrivilegedUserSamAccountNames(domainSid),
            adService.ListAllUsers().Select(u => u.SamAccountName));

    public static ProtectedAccountPolicy Create(IEnumerable<string> privilegedSamAccountNames, IEnumerable<string> allSamAccountNames) =>
        new(new HashSet<string>(privilegedSamAccountNames.Select(s => s.Trim()), StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(allSamAccountNames.Select(s => s.Trim()), StringComparer.OrdinalIgnoreCase),
            null);

    public static ProtectedAccountPolicy FailClosed(string reason) =>
        new(new HashSet<string>(StringComparer.OrdinalIgnoreCase), new HashSet<string>(StringComparer.OrdinalIgnoreCase), reason);

    /// <summary>Why <paramref name="samAccountName"/> is protected, or null if it isn't.</summary>
    public string? GetProtectionReason(string? samAccountName)
    {
        var sam = samAccountName?.Trim() ?? "";
        if (sam.Length == 0)
        {
            return null;
        }
        if (_failClosedReason is not null)
        {
            return _failClosedReason;
        }
        if (_privileged.Contains(sam))
        {
            return "member of a privileged AD group";
        }
        if (IsAdminStyleName(sam, _allSamAccountNames))
        {
            return "administrative account name";
        }
        return null;
    }

    public bool IsProtected(string? samAccountName) => GetProtectionReason(samAccountName) is not null;

    public static bool IsAdminStyleName(string samAccountName, IReadOnlySet<string> allSamAccountNames)
    {
        var sam = samAccountName.Trim();
        if (sam.Contains("admin", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return sam.Length > 2
            && sam.StartsWith("su", StringComparison.OrdinalIgnoreCase)
            && allSamAccountNames.Contains(sam[2..]);
    }
}
