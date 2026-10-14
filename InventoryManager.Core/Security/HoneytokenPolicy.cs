namespace InventoryManager.Core.Security;

/// <summary>
/// Decoy AD objects that no legitimate workflow should ever touch. Populate <see cref="Groups"/>
/// with a bait group (e.g. an empty, scary-looking group named like "Domain Admins - Legacy" that
/// nothing real uses) and <see cref="Usernames"/> with a bait account. Any onboarding grant,
/// transfer, or offboard that targets one is almost certainly an attacker or a compromised
/// operator probing the tool - the action is refused AND a Critical <see cref="SecurityEventType.HoneytokenTripped"/>
/// event is raised (see the wiring in OnboardingService).
///
/// This is a DETECTIVE control, not a cage: a client-side app can't truly trap an operator who can
/// bypass it via ADUC. Its value is the high-signal alert - a honeytoken trip has near-zero false
/// positives because nothing legitimate references these names.
///
/// Fail-safe: an empty list matches nothing, so the feature is inert (no false trips) until you
/// name real decoys. Comparison is case-insensitive.
/// </summary>
public static class HoneytokenPolicy
{
    /// <summary>Decoy group names. EMPTY by default - add your bait group name(s) here (or supply
    /// them per-call for testing). Example: "Domain Admins (DO NOT USE)".</summary>
    public static readonly IReadOnlyCollection<string> Groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Decoys - RESERVED NAMES. Never create a real, in-use group with any of these. Each must
        // exist as an empty bait object in AD (see the hardening runbook) so it shows up in the
        // onboarding group picker and is enticing to an attacker, but nothing legitimate grants it.
        "Domain Admins - Legacy",
        "Tier0-Admins",
    };

    /// <summary>Decoy user sAMAccountNames. EMPTY by default.</summary>
    public static readonly IReadOnlyCollection<string> Usernames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Decoy accounts - RESERVED sAMAccountNames. Create each as a disabled, SACL-audited bait
        // account in AD (see the runbook). Any Transfer/Offboard against one is near-certainly
        // hostile - nothing legitimate should ever touch them.
        "da-breakglass",
        "svc-admbackup",
    };

    public static bool IsHoneytokenGroup(string? groupName, IReadOnlyCollection<string>? groups = null) =>
        Matches(groupName, groups ?? Groups);

    public static bool IsHoneytokenUser(string? username, IReadOnlyCollection<string>? usernames = null) =>
        Matches(username, usernames ?? Usernames);

    private static bool Matches(string? value, IReadOnlyCollection<string> set)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }
        return set.Any(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase));
    }
}
