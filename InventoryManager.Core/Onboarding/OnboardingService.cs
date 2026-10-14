using InventoryManager.Core.Ad;
using InventoryManager.Core.Security;

namespace InventoryManager.Core.Onboarding;

/// <summary>
/// Direct port of the PowerShell build's Show-OnboardingWizard $doFinish scriptblock: two hard
/// stops (OU allow-list, Manager required) independent of whatever the picker UI already
/// filtered, a single New-ADUser-equivalent call so the account is never briefly incomplete, then
/// per-group privileged-group re-checks (never trust the picker alone) and a best-effort computer
/// Description sync. Takes IAdService rather than talking to AD directly, so this - the
/// highest-risk, most novel logic in the wizard - can be unit-tested against a fake AD without
/// ever risking a real write, the same discipline the PowerShell build followed via isolated
/// logic tests before every AD-touching feature shipped.
/// </summary>
public sealed class OnboardingService(
    IAdService adService,
    IReadOnlyCollection<string>? allowedPrivilegedGroupGranters = null,
    ISecurityAudit? audit = null,
    IReadOnlyCollection<string>? honeytokenGroups = null,
    IReadOnlyCollection<string>? honeytokenUsers = null)
{
    private readonly ISecurityAudit _audit = audit ?? NullSecurityAudit.Instance;
    private readonly IReadOnlyCollection<string> _honeytokenGroups = honeytokenGroups ?? HoneytokenPolicy.Groups;
    private readonly IReadOnlyCollection<string> _honeytokenUsers = honeytokenUsers ?? HoneytokenPolicy.Usernames;

    public OnboardingResult CreateUser(OnboardingRequest request)
    {
        // Hard stop, independent of the OU picker's own filtering - the account is never created
        // outside the allowed OU subtree, no matter how the request got built.
        if (!OuPolicy.IsAllowedForNewUsers(request.OrganizationalUnitDn))
        {
            _audit.Record(SecurityEventType.OuPolicyBlocked, SecuritySeverity.Warning, request.SuUsername,
                request.OrganizationalUnitDn,
                $"Onboarding of '{request.SamAccountName}' blocked: OU '{request.OrganizationalUnitDn}' is outside the allowed subtree.");
            throw new OnboardingBlockedException(
                $"The selected Organizational Unit is not allowed for new users. Users may only be created in a sub-OU of '{OuPolicy.AllowedUserOuBaseDn}' - never in that OU directly. Nothing was changed.");
        }
        // Same kind of hard stop as the OU check above - Manager is required by the wizard's own
        // step validation already, but this is the last line of defense before the AD write.
        if (string.IsNullOrWhiteSpace(request.ManagerDistinguishedName))
        {
            throw new OnboardingBlockedException("No Manager was selected. Nothing was changed.");
        }

        // Honeytoken: a brand-new account named like a monitored decoy is a strong tamper signal.
        if (HoneytokenPolicy.IsHoneytokenUser(request.SamAccountName, _honeytokenUsers))
        {
            _audit.Record(SecurityEventType.HoneytokenTripped, SecuritySeverity.Critical, request.SuUsername,
                request.SamAccountName,
                $"Onboarding attempted to create honeytoken account '{request.SamAccountName}'. Refused.");
            throw new OnboardingBlockedException("This account name is a monitored decoy (honeytoken) and cannot be created here. This attempt has been logged and alerted.");
        }

        var fullName = $"{request.FirstName} {request.LastName}".Trim();
        var upn = $"{request.SamAccountName}@{request.UpnSuffix}";

        // Who created this account, and why - stamped into the AD "info" attribute so a manager
        // who opens the profile later can see when it was created, under what ticket, and by
        // whom, without needing to go dig through this app's own logs.
        var creatorDisplay = AdActorNameResolver.GetDisplayName(adService, request.SuUsername);
        var infoNote = AdNoteFormatter.FormatEntry($"Onboarded by {creatorDisplay} - Ticket # {request.TicketNumber}");

        var newUserRequest = new NewAdUserRequest(
            GivenName: request.FirstName,
            Surname: request.LastName,
            SamAccountName: request.SamAccountName,
            UserPrincipalName: upn,
            Password: request.Password,
            OrganizationalUnitDn: request.OrganizationalUnitDn,
            ManagerDistinguishedName: request.ManagerDistinguishedName,
            Description: request.Description,
            Office: request.Office,
            JobTitle: request.JobTitle,
            Department: request.Department,
            Company: request.Company,
            LogonScript: OnboardingRequest.DefaultLogonScript,
            InfoNote: infoNote,
            MustChangePasswordAtLogon: request.MustChangePasswordAtLogon,
            ProvenanceMarkerAttribute: OnboardingMarker.Attribute,
            ProvenanceMarkerValue: OnboardingMarker.Value);

        var createdUser = adService.CreateUser(newUserRequest);

        var domainSid = adService.GetDomainSid();
        var groupOutcomes = new List<OnboardingGroupOutcome>();
        foreach (var groupName in request.GroupNames)
        {
            // Hard stop per group, independent of the picker's own filtering above - never add
            // the new account to a privileged group unless THIS session's own SU account is one
            // of the named "super SU" accounts allowed to grant them (see
            // PrivilegedGroupGrantPolicy) - no matter how the group list got populated. The checks
            // themselves (honeytoken, privileged SID, nesting, allow-list) live in GroupGrantGuard,
            // shared with Copy User Permissions.
            var decision = GroupGrantGuard.Evaluate(adService, groupName, domainSid, request.SuUsername,
                request.SamAccountName, $"Onboarding of '{request.SamAccountName}'",
                allowedPrivilegedGroupGranters, _honeytokenGroups, _audit);
            if (!decision.Allowed)
            {
                groupOutcomes.Add(new OnboardingGroupOutcome(groupName, false, decision.RefusalReason!));
                continue;
            }
            try
            {
                adService.AddUserToGroup(request.SamAccountName, groupName);
                groupOutcomes.Add(new OnboardingGroupOutcome(groupName, true, "Added."));
            }
            catch (Exception ex)
            {
                groupOutcomes.Add(new OnboardingGroupOutcome(groupName, false, $"Could NOT be added: {ex.Message}"));
            }
        }

        var computerSynced = false;
        if (request.ComputerFoundInAd)
        {
            try
            {
                adService.SetComputerDescription(request.ComputerName, $"{fullName}***");
                computerSynced = true;
            }
            catch
            {
                // Saved locally regardless (the caller still records the inventory row) - AD
                // sync failures never block the primary local change, same rule the PowerShell
                // build applies everywhere it syncs a computer Description.
            }
        }

        return new OnboardingResult(createdUser, fullName, groupOutcomes, computerSynced);
    }
}
