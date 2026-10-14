using InventoryManager.Core.Ad;
using InventoryManager.Core.Security;

namespace InventoryManager.Core.Transfer;

/// <summary>
/// Direct port of the PowerShell build's Invoke-TransferUser: each requested change (Manager, OU,
/// Description, Office) is attempted independently so one failure never blocks the others, the OU
/// move is gated by the same OuPolicy allow-list Onboarding uses, and a single AD Notes entry
/// summarizes everything that changed (including the Computer swap, which this class doesn't
/// perform itself - see ComputerIdentityHandoff - but does include in the summary since the
/// caller applies it separately before or after calling this).
/// </summary>
public sealed class TransferService(
    IAdService adService,
    ISecurityAudit? audit = null,
    IReadOnlyCollection<string>? honeytokenUsers = null)
{
    private readonly ISecurityAudit _audit = audit ?? NullSecurityAudit.Instance;
    private readonly IReadOnlyCollection<string> _honeytokenUsers = honeytokenUsers ?? HoneytokenPolicy.Usernames;

    public TransferResult ApplyChanges(TransferRequest request)
    {
        // Honeytoken: any attempt to modify a monitored decoy account is a strong tamper signal.
        if (HoneytokenPolicy.IsHoneytokenUser(request.SamAccountName, _honeytokenUsers))
        {
            _audit.Record(SecurityEventType.HoneytokenTripped, SecuritySeverity.Critical, request.SuUsername,
                request.SamAccountName,
                $"Transfer attempted against honeytoken account '{request.SamAccountName}'. Refused; nothing changed.");
            return new TransferResult(
                [new TransferFieldOutcome("Honeytoken", false,
                    "Refused - this is a monitored decoy (honeytoken) account. This attempt has been logged and alerted. Nothing was changed.")],
                false, []);
        }

        var outcomes = new List<TransferFieldOutcome>();
        var changeLines = new List<string>();

        if (request.ManagerChanged)
        {
            changeLines.Add($"Manager: {request.CurrentManagerDisplay} -> {request.NewManagerDisplay}");
            try
            {
                adService.SetUserManager(request.SamAccountName, request.NewManagerDn ?? "");
                outcomes.Add(new TransferFieldOutcome("Manager", true, "Changed."));
            }
            catch (Exception ex)
            {
                outcomes.Add(new TransferFieldOutcome("Manager", false, $"Could NOT change: {ex.Message}"));
            }
        }

        if (request.OuChanged)
        {
            changeLines.Add($"OU: {request.CurrentOuDn} -> {request.NewOuDn}");
            if (!OuPolicy.IsAllowedForNewUsers(request.NewOuDn))
            {
                _audit.Record(SecurityEventType.OuPolicyBlocked, SecuritySeverity.Warning, request.SuUsername,
                    request.NewOuDn ?? "",
                    $"Transfer of '{request.SamAccountName}' blocked: OU '{request.NewOuDn}' is outside the allowed subtree.");
                outcomes.Add(new TransferFieldOutcome("OU", false, $"Refused - '{request.NewOuDn}' is not an allowed OU."));
            }
            else
            {
                try
                {
                    adService.MoveUser(request.CurrentUserDistinguishedName, request.NewOuDn!);
                    outcomes.Add(new TransferFieldOutcome("OU", true, "Moved."));
                }
                catch (Exception ex)
                {
                    outcomes.Add(new TransferFieldOutcome("OU", false, $"Could NOT move: {ex.Message}"));
                }
            }
        }

        if (request.ComputerChanged)
        {
            changeLines.Add($"Computer: {request.CurrentDeviceName} -> {request.NewDeviceName}");
        }

        if (request.DescriptionChanged)
        {
            changeLines.Add($"Description: '{request.CurrentDescription}' -> '{request.NewDescription}'");
            try
            {
                adService.SetUserDescription(request.SamAccountName, request.NewDescription);
                outcomes.Add(new TransferFieldOutcome("Description", true, "Changed."));
            }
            catch (Exception ex)
            {
                outcomes.Add(new TransferFieldOutcome("Description", false, $"Could NOT change: {ex.Message}"));
            }
        }

        if (request.OfficeChanged)
        {
            changeLines.Add($"Office: '{request.CurrentOffice}' -> '{request.NewOffice}'");
            try
            {
                adService.SetUserOffice(request.SamAccountName, request.NewOffice);
                outcomes.Add(new TransferFieldOutcome("Office", true, "Changed."));
            }
            catch (Exception ex)
            {
                outcomes.Add(new TransferFieldOutcome("Office", false, $"Could NOT change: {ex.Message}"));
            }
        }

        var noteAppended = false;
        if (request.HasAdUser)
        {
            try
            {
                var actor = AdActorNameResolver.GetDisplayName(adService, request.SuUsername);
                var summary = string.Join("; ", changeLines);
                AdNoteAppender.Append(adService, request.SamAccountName, $"Transferred by {actor} - Ticket # {request.TicketNumber}. Changed: {summary}");
                noteAppended = true;
            }
            catch
            {
                // Notes-write failure never blocks or rolls back the actual changes already made.
            }
        }

        return new TransferResult(outcomes, noteAppended, changeLines);
    }
}
