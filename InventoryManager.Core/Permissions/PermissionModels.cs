using InventoryManager.Core.Ad;

namespace InventoryManager.Core.Permissions;

/// <summary>What kind of access a <see cref="PermissionItem"/> represents. Grouped in the dialog
/// grid in this order.</summary>
public enum PermissionCategory
{
    AdGroup,
    CloudGroup,
    TeamOwner,
    PrivateChannel,
    SharePointSiteGroup,
    MailboxFullAccess,
    MailboxSendAs,
    MailboxSendOnBehalf,
    DistributionList,
    NtfsAce,
    License,
}

public enum PermissionItemStatus
{
    /// <summary>Source has it, target doesn't - can be copied.</summary>
    ToAdd,
    /// <summary>Target already has it - nothing to do.</summary>
    AlreadyHas,
    /// <summary>Must not be copied by this tool (privileged, honeytoken, no license seats...).
    /// StatusReason says why. Never applied, even if a caller passes it in.</summary>
    Blocked,
    /// <summary>Covered by a different category (e.g. an AD-synced group seen from the cloud side
    /// is copied as an AdGroup). Shown for completeness, never applied here.</summary>
    HandledElsewhere,
    /// <summary>Informational only (e.g. dynamic group membership, which follows attributes).</summary>
    Info,
}

/// <summary>
/// One copyable permission found on the source user. <see cref="Key"/> is opaque to everything
/// except the provider that produced it (a group DN, a Graph object id, a folder path...).
/// </summary>
public sealed record PermissionItem(
    string ProviderName,
    PermissionCategory Category,
    string DisplayName,
    string Key,
    string Detail,
    PermissionItemStatus Status,
    string StatusReason = "",
    bool DefaultChecked = true)
{
    public bool CanApply => Status == PermissionItemStatus.ToAdd;
}

public sealed record PermissionApplyResult(PermissionItem Item, bool Success, string Message);

/// <summary>Who is copying what to whom. Source/Target are the AD accounts; cloud providers map
/// them to Entra users themselves.</summary>
public sealed record PermissionCopyContext(AdUserInfo Source, AdUserInfo Target, string SuUsername);

public sealed record PermissionProviderError(string ProviderName, string Message);

public sealed record PermissionScanResult(
    IReadOnlyList<PermissionItem> Items,
    IReadOnlyList<PermissionProviderError> Errors);

public sealed record PermissionCopyReport(
    PermissionCopyContext Context,
    DateTime StartedLocal,
    IReadOnlyList<PermissionApplyResult> Results,
    bool Cancelled)
{
    public int Succeeded => Results.Count(r => r.Success);
    public int Failed => Results.Count(r => !r.Success);
}

/// <summary>Thrown when the copy must not run at all (same user, honeytoken account...). Nothing
/// was changed.</summary>
public sealed class PermissionCopyBlockedException(string message) : Exception(message);

/// <summary>
/// One family of permissions (AD groups, cloud groups, mailboxes, NTFS...). Scan is read-only;
/// Apply grants ONE item and must re-check anything security-relevant at that moment rather than
/// trusting the scan (time passes, and the scan result sits in a UI the operator can tick).
/// </summary>
public interface IPermissionProvider
{
    string Name { get; }
    IReadOnlyList<PermissionItem> Scan(PermissionCopyContext context, IProgress<string>? progress, CancellationToken cancellationToken);
    PermissionApplyResult Apply(PermissionItem item, PermissionCopyContext context);
}
