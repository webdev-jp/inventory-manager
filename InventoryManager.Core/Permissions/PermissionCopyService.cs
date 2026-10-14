using System.Text;
using InventoryManager.Core.Ad;
using InventoryManager.Core.Security;

namespace InventoryManager.Core.Permissions;

/// <summary>
/// Copy User Permissions: scans each provider for what the source has and the target doesn't,
/// then applies only the items the operator ticked. Add-only by design - nothing is ever removed
/// from the target. One provider failing (e.g. M365 sign-in expired) never stops the others; its
/// error is reported alongside everyone else's results.
/// </summary>
public sealed class PermissionCopyService(
    IAdService adService,
    IReadOnlyList<IPermissionProvider> providers,
    ISecurityAudit? audit = null,
    IReadOnlyCollection<string>? honeytokenUsers = null)
{
    private readonly ISecurityAudit _audit = audit ?? NullSecurityAudit.Instance;
    private readonly IReadOnlyCollection<string> _honeytokenUsers = honeytokenUsers ?? HoneytokenPolicy.Usernames;

    public PermissionScanResult Scan(PermissionCopyContext context, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        EnsureAllowed(context);
        var items = new List<PermissionItem>();
        var errors = new List<PermissionProviderError>();
        foreach (var provider in providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                items.AddRange(provider.Scan(context, progress, cancellationToken));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                errors.Add(new PermissionProviderError(provider.Name, ex.Message));
            }
        }
        return new PermissionScanResult(items, errors);
    }

    /// <summary>Applies <paramref name="selected"/>. Anything not <see cref="PermissionItem.CanApply"/>
    /// is refused regardless of what the UI passed in. Cancellation stops between items; what was
    /// already applied is still reported, noted in AD and audited.</summary>
    public PermissionCopyReport Apply(PermissionCopyContext context, IReadOnlyList<PermissionItem> selected,
        IProgress<string>? progress, CancellationToken cancellationToken)
    {
        EnsureAllowed(context);
        var started = DateTime.Now;
        var byName = providers.ToDictionary(p => p.Name, StringComparer.Ordinal);
        var results = new List<PermissionApplyResult>();
        var cancelled = false;

        for (var i = 0; i < selected.Count; i++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }
            var item = selected[i];
            progress?.Report($"({i + 1}/{selected.Count}) {item.DisplayName}");
            if (!item.CanApply)
            {
                results.Add(new PermissionApplyResult(item, false, $"Not applied - status is {item.Status}."));
                continue;
            }
            if (!byName.TryGetValue(item.ProviderName, out var provider))
            {
                results.Add(new PermissionApplyResult(item, false, $"No provider '{item.ProviderName}' is loaded."));
                continue;
            }
            try
            {
                results.Add(provider.Apply(item, context));
            }
            catch (Exception ex)
            {
                results.Add(new PermissionApplyResult(item, false, $"Failed: {ex.Message}"));
            }
        }

        var report = new PermissionCopyReport(context, started, results, cancelled);
        RecordOutcome(report);
        return report;
    }

    private void EnsureAllowed(PermissionCopyContext context)
    {
        var src = context.Source.SamAccountName;
        var tgt = context.Target.SamAccountName;
        if (string.Equals(src, tgt, StringComparison.OrdinalIgnoreCase))
        {
            throw new PermissionCopyBlockedException("Source and target are the same user. Nothing was changed.");
        }
        // Honeytoken: a decoy account nothing legitimate touches - reading FROM one or granting TO
        // one are both strong tamper signals.
        foreach (var sam in new[] { src, tgt })
        {
            if (HoneytokenPolicy.IsHoneytokenUser(sam, _honeytokenUsers))
            {
                _audit.Record(SecurityEventType.HoneytokenTripped, SecuritySeverity.Critical, context.SuUsername, sam,
                    $"Copy User Permissions attempted with honeytoken account '{sam}' ('{src}' -> '{tgt}'). Refused; nothing changed.");
                throw new PermissionCopyBlockedException("One of these accounts is a monitored decoy (honeytoken). This attempt has been logged and alerted. Nothing was changed.");
            }
        }
    }

    private void RecordOutcome(PermissionCopyReport report)
    {
        var ctx = report.Context;
        if (report.Results.Count == 0)
        {
            return;
        }
        var summary = string.Join(", ", report.Results.Where(r => r.Success)
            .GroupBy(r => r.Item.Category)
            .Select(g => $"{g.Count()} {CategoryLabel(g.Key)}"));
        _audit.Record(SecurityEventType.PermissionsCopied, report.Failed > 0 ? SecuritySeverity.Warning : SecuritySeverity.Info,
            ctx.SuUsername, ctx.Target.SamAccountName,
            $"Copied permissions from '{ctx.Source.SamAccountName}' to '{ctx.Target.SamAccountName}': {report.Succeeded} succeeded ({summary}), {report.Failed} failed{(report.Cancelled ? ", cancelled part-way" : "")}.");

        if (report.Succeeded == 0)
        {
            return;
        }
        // Running history on the target account itself, same as Onboarding/Transfer. Best-effort -
        // the grants already happened; a note failure must not look like the copy failed.
        try
        {
            var actor = AdActorNameResolver.GetDisplayName(adService, ctx.SuUsername);
            var sourceName = $"{ctx.Source.GivenName} {ctx.Source.Surname}".Trim();
            AdNoteAppender.Append(adService, ctx.Target.SamAccountName,
                $"Permissions copied from {sourceName} ({ctx.Source.SamAccountName}) by {actor}: {summary}");
        }
        catch
        {
            // Reported through the CSV/log instead; see PermissionCopyReport.
        }
    }

    public static string CategoryLabel(PermissionCategory category) => category switch
    {
        PermissionCategory.AdGroup => "AD group(s)",
        PermissionCategory.CloudGroup => "M365/Teams group(s)",
        PermissionCategory.TeamOwner => "Team ownership(s)",
        PermissionCategory.PrivateChannel => "private channel(s)",
        PermissionCategory.SharePointSiteGroup => "SharePoint site permission(s)",
        PermissionCategory.MailboxFullAccess => "mailbox Full Access",
        PermissionCategory.MailboxSendAs => "Send As",
        PermissionCategory.MailboxSendOnBehalf => "Send on Behalf",
        PermissionCategory.DistributionList => "distribution list(s)",
        PermissionCategory.NtfsAce => "folder permission(s)",
        PermissionCategory.License => "license(s)",
        _ => category.ToString(),
    };

    /// <summary>The "what was applied" record - enough to undo a copy by hand. Formula-injection
    /// safe via Csv.WriteRow, since group/folder names come from AD and M365.</summary>
    public static string BuildCsv(PermissionCopyReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Csv.WriteRow(["Timestamp", "Source", "Target", "Category", "Item", "Detail", "Key", "Result", "Message"]));
        var stamp = report.StartedLocal.ToString("yyyy-MM-dd HH:mm:ss");
        foreach (var r in report.Results)
        {
            sb.AppendLine(Csv.WriteRow([stamp, report.Context.Source.SamAccountName, report.Context.Target.SamAccountName,
                r.Item.Category.ToString(), r.Item.DisplayName, r.Item.Detail, r.Item.Key, r.Success ? "Success" : "Failed", r.Message]));
        }
        return sb.ToString();
    }

    /// <summary>A scan exported before anything was applied: what the source has and what each
    /// row's status was.</summary>
    public static string BuildScanCsv(PermissionCopyContext context, IReadOnlyList<PermissionItem> items)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Csv.WriteRow(["Timestamp", "Source", "Target", "Category", "Item", "Detail", "Key", "Status", "Reason"]));
        var stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        foreach (var i in items)
        {
            sb.AppendLine(Csv.WriteRow([stamp, context.Source.SamAccountName, context.Target.SamAccountName,
                i.Category.ToString(), i.DisplayName, i.Detail, i.Key, i.Status.ToString(), i.StatusReason]));
        }
        return sb.ToString();
    }
}
