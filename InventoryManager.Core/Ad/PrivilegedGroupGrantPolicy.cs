namespace InventoryManager.Core.Ad;

/// <summary>
/// Restricts which authenticated SU account may grant a PRIVILEGED group (see
/// PrivilegedGroupPolicy - Domain Admins, Enterprise Admins, Account/Server/Backup Operators,
/// etc.) to a user during Onboarding. Added per Francois's explicit instruction: every SU account
/// may onboard a new user and grant ordinary groups (Domain Users, distribution lists, ordinary
/// security groups) exactly as before - only a short, named list of "super SU" accounts may also
/// hand out a privileged/domain-level group on top of that. An SU account not on this list can
/// still create the account; the privileged group specifically is refused, same as it already is
/// for everyone today.
///
/// Fails CLOSED: an empty or not-yet-populated list means NO SU account may grant a privileged
/// group (today's existing behavior, unchanged) rather than everyone being able to - the same
/// fail-closed default <see cref="PrivilegedGroupPolicy.IsPrivileged"/> already uses for an
/// unresolvable group.
/// </summary>
public static class PrivilegedGroupGrantPolicy
{
    /// <summary>
    /// The only SU accounts permitted to grant a privileged group during Onboarding.
    /// SamAccountName, case-insensitive. Named by Francois on 2026-09-15.
    /// </summary>
    public static readonly IReadOnlyCollection<string> AllowedPrivilegedGroupGranters = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "supattersonb",
        "sudmorgan",
    };

    /// <summary>
    /// True only if <paramref name="suUsername"/> case-insensitively matches an entry in
    /// <paramref name="allowList"/> (defaults to the real <see cref="AllowedPrivilegedGroupGranters"/>
    /// list). Takes the allow-list as a parameter, not just ambient state, so it can be unit tested
    /// deterministically regardless of what the real production list currently holds.
    /// </summary>
    public static bool IsAllowed(string? suUsername, IReadOnlyCollection<string>? allowList = null)
    {
        if (string.IsNullOrWhiteSpace(suUsername))
        {
            return false;
        }
        var list = allowList ?? AllowedPrivilegedGroupGranters;
        return list.Any(u => string.Equals(u, suUsername, StringComparison.OrdinalIgnoreCase));
    }
}
