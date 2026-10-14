using InventoryManager.Core.Ad;
using InventoryManager.Core.Security;

namespace InventoryManager.Core.Permissions;

/// <summary>
/// On-prem AD group memberships: the source's direct memberOf minus the target's. Every grant goes
/// through <see cref="GroupGrantGuard"/> - the same honeytoken/privileged/nesting/allow-list path
/// Onboarding uses - at scan time as a silent preview and again, audited, at apply time.
/// The primary group (Domain Users) isn't in memberOf and is ignored; every account has it anyway.
/// </summary>
public sealed class AdGroupPermissionProvider(
    IAdService adService,
    IReadOnlyCollection<string>? allowedPrivilegedGroupGranters = null,
    ISecurityAudit? audit = null,
    IReadOnlyCollection<string>? honeytokenGroups = null) : IPermissionProvider
{
    private readonly ISecurityAudit _audit = audit ?? NullSecurityAudit.Instance;
    private readonly IReadOnlyCollection<string> _honeytokenGroups = honeytokenGroups ?? HoneytokenPolicy.Groups;

    public string Name => "Active Directory groups";

    public IReadOnlyList<PermissionItem> Scan(PermissionCopyContext context, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        progress?.Report("Reading AD group memberships...");
        var sourceDns = adService.GetUserGroupDistinguishedNames(context.Source.SamAccountName);
        var targetDns = new HashSet<string>(adService.GetUserGroupDistinguishedNames(context.Target.SamAccountName), StringComparer.OrdinalIgnoreCase);
        var domainSid = adService.GetDomainSid();

        var items = new List<PermissionItem>();
        foreach (var dn in sourceDns)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var group = adService.GetGroupByDistinguishedName(dn);
            if (group is null)
            {
                items.Add(new PermissionItem(Name, PermissionCategory.AdGroup, DisplayNameFromDn(dn), dn, "",
                    PermissionItemStatus.Blocked, "Could not read this group from AD.", DefaultChecked: false));
                continue;
            }

            var detail = $"{(group.IsSecurityGroup ? "Security" : "Distribution")} group, {group.Scope}";
            if (targetDns.Contains(dn))
            {
                items.Add(new PermissionItem(Name, PermissionCategory.AdGroup, group.Name, dn, detail,
                    PermissionItemStatus.AlreadyHas, DefaultChecked: false));
                continue;
            }

            var decision = GroupGrantGuard.Preview(adService, group.SamAccountName, domainSid, context.SuUsername,
                allowedPrivilegedGroupGranters, _honeytokenGroups);
            items.Add(decision.Allowed
                ? new PermissionItem(Name, PermissionCategory.AdGroup, group.Name, dn, detail, PermissionItemStatus.ToAdd)
                : new PermissionItem(Name, PermissionCategory.AdGroup, group.Name, dn, detail,
                    PermissionItemStatus.Blocked, decision.RefusalReason ?? "Refused.", DefaultChecked: false));
        }
        return items.OrderBy(i => i.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public PermissionApplyResult Apply(PermissionItem item, PermissionCopyContext context)
    {
        // Re-read the group fresh rather than trusting anything the scan captured.
        var group = adService.GetGroupByDistinguishedName(item.Key);
        if (group is null)
        {
            return new PermissionApplyResult(item, false, "Group could no longer be read from AD.");
        }
        var targetSam = context.Target.SamAccountName;
        if (adService.GetUserGroupDistinguishedNames(targetSam).Contains(item.Key, StringComparer.OrdinalIgnoreCase))
        {
            return new PermissionApplyResult(item, true, "Already a member.");
        }

        var decision = GroupGrantGuard.Evaluate(adService, group.SamAccountName, adService.GetDomainSid(), context.SuUsername,
            targetSam, $"Permission copy from '{context.Source.SamAccountName}' to '{targetSam}'",
            allowedPrivilegedGroupGranters, _honeytokenGroups, _audit);
        if (!decision.Allowed)
        {
            return new PermissionApplyResult(item, false, decision.RefusalReason ?? "Refused.");
        }

        adService.AddUserToGroupByDistinguishedName(targetSam, item.Key);
        return new PermissionApplyResult(item, true, "Added.");
    }

    /// <summary>"CN=HR Staff,OU=Groups,DC=x" -> "HR Staff" (unescaping \, ) for display only.</summary>
    internal static string DisplayNameFromDn(string dn)
    {
        var first = DistinguishedNameHelper.GetFirstRdnValue(dn);
        return string.IsNullOrEmpty(first) ? dn : first;
    }
}
