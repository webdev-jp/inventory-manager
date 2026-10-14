using InventoryManager.Core.Security;

namespace InventoryManager.Core.Ad;

/// <summary>Outcome of <see cref="GroupGrantGuard.Evaluate"/>: Allowed, or refused with the
/// operator-facing reason.</summary>
public sealed record GroupGrantDecision(bool Allowed, string? RefusalReason)
{
    public static readonly GroupGrantDecision Allow = new(true, null);
}

/// <summary>
/// The one security path every "add this user to an AD group" feature goes through - extracted
/// unchanged from OnboardingService's per-group loop so Copy User Permissions can't drift from it:
/// honeytoken group refusal (Critical alert), direct privileged SID/RID check (fails closed on an
/// unresolvable SID), privilege-via-nesting check, then the super-SU allow-list. Every refusal and
/// every authorized privileged grant is audited.
/// </summary>
public static class GroupGrantGuard
{
    /// <param name="groupName">sAMAccountName of the group - the privileged-group checks key on it.</param>
    /// <param name="targetSam">The account being granted the group (used in audit details only).</param>
    /// <param name="operationLabel">Prefix for the honeytoken audit detail, e.g. "Onboarding of 'jdoe'".</param>
    public static GroupGrantDecision Evaluate(
        IAdService adService,
        string groupName,
        string? domainSid,
        string suUsername,
        string targetSam,
        string operationLabel,
        IReadOnlyCollection<string>? allowedPrivilegedGroupGranters,
        IReadOnlyCollection<string> honeytokenGroups,
        ISecurityAudit audit)
    {
        // Honeytoken: a decoy group nothing legitimate references. Refuse + Critical alert.
        if (HoneytokenPolicy.IsHoneytokenGroup(groupName, honeytokenGroups))
        {
            audit.Record(SecurityEventType.HoneytokenTripped, SecuritySeverity.Critical, suUsername,
                groupName,
                $"{operationLabel} attempted to grant honeytoken group '{groupName}'. Refused.");
            return new GroupGrantDecision(false,
                "Refused - this is a monitored decoy (honeytoken) group and must never be granted. This attempt has been logged and alerted.");
        }

        var groupSid = adService.GetGroupSid(groupName);
        var isPrivileged = PrivilegedGroupPolicy.IsPrivileged(groupName, groupSid, domainSid);
        var viaNesting = false;
        if (!isPrivileged)
        {
            // Supplemental: an ordinary-looking group that is itself nested (at any depth) into
            // a privileged group confers that privilege, but its own SID isn't a privileged
            // RID - so the direct check above would miss it. Best-effort; if the lookup can't
            // run, we simply fall back to the direct check rather than block a normal grant.
            var parentSids = adService.GetGroupParentGroupSids(groupName);
            if (parentSids.Count > 0)
            {
                var privilegedSids = PrivilegedGroupPolicy.GetPrivilegedGroupSids(domainSid);
                if (parentSids.Any(privilegedSids.Contains))
                {
                    isPrivileged = true;
                    viaNesting = true;
                }
            }
        }
        if (!isPrivileged)
        {
            return GroupGrantDecision.Allow;
        }

        var kind = viaNesting ? "nested-privileged" : "privileged";
        if (!PrivilegedGroupGrantPolicy.IsAllowed(suUsername, allowedPrivilegedGroupGranters))
        {
            audit.Record(
                viaNesting ? SecurityEventType.NestedPrivilegeRefused : SecurityEventType.PrivilegedGrantRefused,
                SecuritySeverity.Warning, suUsername, groupName,
                $"Refused granting {kind} group '{groupName}' to '{targetSam}' - SU account not authorized.");
            return new GroupGrantDecision(false,
                "Refused - it is a privileged/domain-level group and this SU account is not authorized to grant privileged groups. Add it manually if this is genuinely intended.");
        }
        // Authorized, but a privileged grant is always worth a record.
        audit.Record(SecurityEventType.PrivilegedGrantAllowed, SecuritySeverity.Warning, suUsername,
            groupName,
            $"Authorized SU granted {kind} group '{groupName}' to '{targetSam}'.");
        return GroupGrantDecision.Allow;
    }

    /// <summary>
    /// Read-only preview of <see cref="Evaluate"/> for a scan screen: says whether the grant WOULD
    /// be refused, without writing any audit record (nothing has been attempted yet). The real
    /// Evaluate still runs, and audits, at apply time.
    /// </summary>
    public static GroupGrantDecision Preview(
        IAdService adService,
        string groupName,
        string? domainSid,
        string suUsername,
        IReadOnlyCollection<string>? allowedPrivilegedGroupGranters,
        IReadOnlyCollection<string> honeytokenGroups) =>
        Evaluate(adService, groupName, domainSid, suUsername, "", "", allowedPrivilegedGroupGranters, honeytokenGroups, NullSecurityAudit.Instance);
}
