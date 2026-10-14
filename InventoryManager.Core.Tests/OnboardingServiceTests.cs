using InventoryManager.Core.Ad;
using InventoryManager.Core.Onboarding;
using InventoryManager.Core.Security;

namespace InventoryManager.Core.Tests;

public class OnboardingServiceTests
{
    private const string AllowedOu = "OU=Main Office,OU=Domain Users,DC=contoso,DC=local";

    private static OnboardingRequest MakeRequest(
        string ou = AllowedOu,
        string managerDn = "CN=Boss,OU=X,DC=contoso,DC=local",
        IReadOnlyList<string>? groups = null,
        bool computerFound = true) => new(
        TicketNumber: "TCK-1001",
        FirstName: "Jane",
        LastName: "Doe",
        Description: "Clinician",
        Office: "Main Office",
        JobTitle: "Clinician II",
        Department: "Clinical Services",
        Company: "Contoso",
        SamAccountName: "doej",
        UpnSuffix: "contoso.com",
        Password: "TempP@ss123",
        MustChangePasswordAtLogon: true,
        OrganizationalUnitDn: ou,
        ManagerDistinguishedName: managerDn,
        ManagerDisplay: "Boss Person (bossp)",
        GroupNames: groups ?? [],
        ComputerName: "LAPTOP-01",
        ComputerFoundInAd: computerFound,
        SuUsername: "suadmin");

    private static FakeAdService MakeAdWithSu()
    {
        var ad = new FakeAdService();
        ad.Users["suadmin"] = new AdUserInfo("suadmin", "CN=SU Admin,DC=x", "SU", "Admin", true, null, "", "", "");
        ad.Computers["LAPTOP-01"] = new AdComputerInfo("LAPTOP-01", "CN=LAPTOP-01,DC=x", true, "");
        return ad;
    }

    [Fact]
    public void CreateUser_DisallowedOu_ThrowsAndNeverCallsCreateUser()
    {
        var ad = MakeAdWithSu();
        var service = new OnboardingService(ad);
        var request = MakeRequest(ou: "OU=Domain Users,DC=contoso,DC=local"); // base OU itself

        Assert.Throws<OnboardingBlockedException>(() => service.CreateUser(request));
        Assert.Empty(ad.CreateUserCalls);
    }

    [Fact]
    public void CreateUser_MissingManager_ThrowsAndNeverCallsCreateUser()
    {
        var ad = MakeAdWithSu();
        var service = new OnboardingService(ad);
        var request = MakeRequest(managerDn: "");

        Assert.Throws<OnboardingBlockedException>(() => service.CreateUser(request));
        Assert.Empty(ad.CreateUserCalls);
    }

    [Fact]
    public void CreateUser_HappyPath_CreatesUserWithNoteAndProvenanceMarker()
    {
        var ad = MakeAdWithSu();
        var service = new OnboardingService(ad);
        var result = service.CreateUser(MakeRequest());

        Assert.Single(ad.CreateUserCalls);
        Assert.Equal("doej", result.CreatedUser.SamAccountName);
        Assert.Equal("Jane Doe", result.FullName);
        Assert.Contains("Onboarded by SU Admin (suadmin) - Ticket # TCK-1001", result.CreatedUser.Info);
        // 12-hour clock with AM/PM, e.g. "[9/15/2026 3:45 PM]" - Francois asked for this over a
        // 24-hour timestamp on 2026-09-15.
        Assert.Matches(@"^\[\d{1,2}/\d{1,2}/\d{4} \d{1,2}:\d{2} (AM|PM)\]", result.CreatedUser.Info);
        // The provenance marker (a silent audit trail for "this account was created by Inventory
        // Manager", visible only via ADUC's Attribute Editor tab) must actually reach the AD
        // write call - this was previously unverified since FakeAdService didn't capture it at all.
        Assert.Equal(("comment", "InventoryAccountCreated"), ad.LastProvenanceMarker);
    }

    [Fact]
    public void CreateUser_PassesJobTitleDepartmentCompanyAndDefaultLogonScriptThrough()
    {
        // Job Title/Department/Company (the wizard's new Organization-tab fields) and the fixed
        // logon script default must actually reach the AD write call - Francois, 2026-09-16.
        var ad = MakeAdWithSu();
        var service = new OnboardingService(ad);
        service.CreateUser(MakeRequest());

        Assert.NotNull(ad.LastCreateUserRequest);
        Assert.Equal("Clinician II", ad.LastCreateUserRequest!.JobTitle);
        Assert.Equal("Clinical Services", ad.LastCreateUserRequest.Department);
        Assert.Equal("Contoso", ad.LastCreateUserRequest.Company);
        Assert.Equal(OnboardingRequest.DefaultLogonScript, ad.LastCreateUserRequest.LogonScript);
    }

    [Fact]
    public void CreateUser_ActorLookupFails_FallsBackToRawUsername()
    {
        var ad = new FakeAdService(); // no "suadmin" registered - GetUser returns null
        ad.Computers["LAPTOP-01"] = new AdComputerInfo("LAPTOP-01", "CN=LAPTOP-01,DC=x", true, "");
        var service = new OnboardingService(ad);
        var result = service.CreateUser(MakeRequest());

        Assert.Contains("Onboarded by suadmin - Ticket # TCK-1001", result.CreatedUser.Info);
    }

    [Fact]
    public void CreateUser_PrivilegedGroup_IsRefused_OrdinaryGroup_IsAdded()
    {
        // No allowedPrivilegedGroupGranters override - "suadmin" is not on the real production
        // PrivilegedGroupGrantPolicy list, so this exercises the default (every SU account is
        // refused a privileged group unless explicitly named elsewhere).
        var ad = MakeAdWithSu();
        ad.GroupSids["Domain Admins"] = $"{ad.DomainSid}-512";
        ad.GroupSids["Front Desk Staff"] = $"{ad.DomainSid}-5001";
        var service = new OnboardingService(ad);

        var result = service.CreateUser(MakeRequest(groups: ["Domain Admins", "Front Desk Staff"]));

        var adminOutcome = result.GroupOutcomes.Single(g => g.GroupName == "Domain Admins");
        Assert.False(adminOutcome.Added);
        Assert.Contains("privileged", adminOutcome.Reason, StringComparison.OrdinalIgnoreCase);

        var staffOutcome = result.GroupOutcomes.Single(g => g.GroupName == "Front Desk Staff");
        Assert.True(staffOutcome.Added);
        Assert.Contains(("doej", "Front Desk Staff"), ad.AddToGroupCalls);
        Assert.DoesNotContain(("doej", "Domain Admins"), ad.AddToGroupCalls);
    }

    [Fact]
    public void CreateUser_PrivilegedGroup_ElevatedSuAccount_IsAllowedToGrant()
    {
        // "suadmin" IS explicitly on this service's own allowedPrivilegedGroupGranters override -
        // the mirror image of the test above, proving the bypass actually works for a named
        // "super SU" account rather than only proving the default-deny path.
        var ad = MakeAdWithSu();
        ad.GroupSids["Domain Admins"] = $"{ad.DomainSid}-512";
        var service = new OnboardingService(ad, allowedPrivilegedGroupGranters: ["suadmin"]);

        var result = service.CreateUser(MakeRequest(groups: ["Domain Admins"]));

        var adminOutcome = result.GroupOutcomes.Single(g => g.GroupName == "Domain Admins");
        Assert.True(adminOutcome.Added);
        Assert.Contains(("doej", "Domain Admins"), ad.AddToGroupCalls);
    }

    [Fact]
    public void CreateUser_PrivilegedGroup_RealProductionAllowList_GrantsForNamedSuAccounts()
    {
        // Exercises the real, populated production list (no override) directly - "supattersonb"
        // and "sudmorgan" are the two accounts Francois named as authorized to grant privileged
        // groups; pinning this catches an accidental edit to the real list just as much as the
        // deny-by-default test above catches a regression toward "allow everyone."
        var ad = new FakeAdService();
        ad.Users["supattersonb"] = new AdUserInfo("supattersonb", "CN=SU Patterson,DC=x", "SU", "Patterson", true, null, "", "", "");
        ad.Computers["LAPTOP-01"] = new AdComputerInfo("LAPTOP-01", "CN=LAPTOP-01,DC=x", true, "");
        ad.GroupSids["Domain Admins"] = $"{ad.DomainSid}-512";
        var service = new OnboardingService(ad);
        var request = MakeRequest(groups: ["Domain Admins"]) with { SuUsername = "supattersonb" };

        var result = service.CreateUser(request);

        Assert.True(result.GroupOutcomes.Single(g => g.GroupName == "Domain Admins").Added);
    }

    [Fact]
    public void CreateUser_OrdinaryGroupNestedIntoPrivileged_IsRefusedForUnauthorizedSu()
    {
        // "Front Desk Staff" has an ordinary, non-privileged SID of its own, but it is itself a
        // member of Domain Admins (RID 512) - so granting it confers Domain Admin. A direct
        // SID/RID check on the target group alone would MISS this; the transitive parent-group
        // check must catch it and refuse for an SU account not on the privileged-grant allow-list.
        var ad = MakeAdWithSu();
        ad.GroupSids["Front Desk Staff"] = $"{ad.DomainSid}-5001";
        ad.GroupParentSids["Front Desk Staff"] = [$"{ad.DomainSid}-512"];
        var service = new OnboardingService(ad); // suadmin is NOT an authorized granter

        var result = service.CreateUser(MakeRequest(groups: ["Front Desk Staff"]));

        var outcome = result.GroupOutcomes.Single(g => g.GroupName == "Front Desk Staff");
        Assert.False(outcome.Added);
        Assert.Contains("privileged", outcome.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(("doej", "Front Desk Staff"), ad.AddToGroupCalls);
    }

    [Fact]
    public void CreateUser_OrdinaryGroupNestedIntoPrivileged_IsAllowedForAuthorizedSu()
    {
        var ad = MakeAdWithSu();
        ad.GroupSids["Front Desk Staff"] = $"{ad.DomainSid}-5001";
        ad.GroupParentSids["Front Desk Staff"] = [$"{ad.DomainSid}-512"];
        var service = new OnboardingService(ad, allowedPrivilegedGroupGranters: ["suadmin"]);

        var result = service.CreateUser(MakeRequest(groups: ["Front Desk Staff"]));

        Assert.True(result.GroupOutcomes.Single(g => g.GroupName == "Front Desk Staff").Added);
    }

    [Fact]
    public void CreateUser_HoneytokenGroup_IsRefusedAndAuditedCritical()
    {
        var ad = MakeAdWithSu();
        ad.GroupSids["Domain Admins - Legacy"] = $"{ad.DomainSid}-5099"; // ordinary SID; decoy by name
        var audit = new RecordingSecurityAudit();
        var service = new OnboardingService(ad, audit: audit, honeytokenGroups: ["Domain Admins - Legacy"]);

        var result = service.CreateUser(MakeRequest(groups: ["Domain Admins - Legacy"]));

        var outcome = result.GroupOutcomes.Single(g => g.GroupName == "Domain Admins - Legacy");
        Assert.False(outcome.Added);
        Assert.Contains("honeytoken", outcome.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(("doej", "Domain Admins - Legacy"), ad.AddToGroupCalls);
        Assert.Contains(audit.Events, e => e.Type == SecurityEventType.HoneytokenTripped && e.Severity == SecuritySeverity.Critical);
    }

    [Fact]
    public void CreateUser_PrivilegedRefused_IsAudited()
    {
        var ad = MakeAdWithSu();
        ad.GroupSids["Domain Admins"] = $"{ad.DomainSid}-512";
        var audit = new RecordingSecurityAudit();
        var service = new OnboardingService(ad, audit: audit);

        service.CreateUser(MakeRequest(groups: ["Domain Admins"]));

        Assert.Contains(audit.Events, e => e.Type == SecurityEventType.PrivilegedGrantRefused && e.Target == "Domain Admins");
    }

    [Fact]
    public void CreateUser_NestedPrivilegedRefused_IsAuditedAsNested()
    {
        var ad = MakeAdWithSu();
        ad.GroupSids["Front Desk Staff"] = $"{ad.DomainSid}-5001";
        ad.GroupParentSids["Front Desk Staff"] = [$"{ad.DomainSid}-512"];
        var audit = new RecordingSecurityAudit();
        var service = new OnboardingService(ad, audit: audit);

        service.CreateUser(MakeRequest(groups: ["Front Desk Staff"]));

        Assert.Contains(audit.Events, e => e.Type == SecurityEventType.NestedPrivilegeRefused);
    }

    [Fact]
    public void CreateUser_PrivilegedAllowed_IsAudited()
    {
        var ad = MakeAdWithSu();
        ad.GroupSids["Domain Admins"] = $"{ad.DomainSid}-512";
        var audit = new RecordingSecurityAudit();
        var service = new OnboardingService(ad, allowedPrivilegedGroupGranters: ["suadmin"], audit: audit);

        service.CreateUser(MakeRequest(groups: ["Domain Admins"]));

        Assert.Contains(audit.Events, e => e.Type == SecurityEventType.PrivilegedGrantAllowed && e.Target == "Domain Admins");
    }

    [Fact]
    public void CreateUser_GroupAddThrows_RecordsFailureButDoesNotAbortOtherGroups()
    {
        var ad = MakeAdWithSu();
        ad.GroupSids["Broken Group"] = $"{ad.DomainSid}-6001";
        ad.GroupSids["Good Group"] = $"{ad.DomainSid}-6002";
        ad.GroupsThatFailToAdd.Add("Broken Group");
        var service = new OnboardingService(ad);

        var result = service.CreateUser(MakeRequest(groups: ["Broken Group", "Good Group"]));

        Assert.False(result.GroupOutcomes.Single(g => g.GroupName == "Broken Group").Added);
        Assert.True(result.GroupOutcomes.Single(g => g.GroupName == "Good Group").Added);
    }

    [Fact]
    public void CreateUser_ComputerFoundInAd_SyncsDescription()
    {
        var ad = MakeAdWithSu();
        var service = new OnboardingService(ad);
        var result = service.CreateUser(MakeRequest());

        Assert.True(result.ComputerDescriptionSynced);
        Assert.Contains(("LAPTOP-01", "Jane Doe***"), ad.ComputerDescriptionCalls);
    }

    [Fact]
    public void CreateUser_ComputerNotFoundInAd_SkipsDescriptionSyncWithoutFailing()
    {
        var ad = MakeAdWithSu();
        var service = new OnboardingService(ad);
        var result = service.CreateUser(MakeRequest(computerFound: false));

        Assert.False(result.ComputerDescriptionSynced);
        Assert.Empty(ad.ComputerDescriptionCalls);
    }
}
