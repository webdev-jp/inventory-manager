using InventoryManager.Core.Ad;
using InventoryManager.Core.Transfer;
using InventoryManager.Core.Security;

namespace InventoryManager.Core.Tests;

public class TransferServiceTests
{
    private static TransferRequest MakeRequest(
        bool hasAdUser = true,
        bool managerChanged = false, bool ouChanged = false, bool computerChanged = false,
        bool descriptionChanged = false, bool officeChanged = false,
        string newOuDn = "OU=Northside,OU=Domain Users,DC=contoso,DC=local") => new(
        SamAccountName: "doej",
        HasAdUser: hasAdUser,
        CurrentUserDistinguishedName: DoeJaneDn,
        TicketNumber: "TCK-2001",
        ManagerChanged: managerChanged,
        NewManagerDn: "CN=Boss,OU=X,DC=contoso,DC=local",
        NewManagerDisplay: "Boss Person (bossp)",
        CurrentManagerDisplay: "Old Boss (oldboss)",
        OuChanged: ouChanged,
        NewOuDn: newOuDn,
        CurrentOuDn: "OU=Main Office,OU=Domain Users,DC=contoso,DC=local",
        DescriptionChanged: descriptionChanged,
        NewDescription: "New role",
        CurrentDescription: "Old role",
        OfficeChanged: officeChanged,
        NewOffice: "New Office",
        CurrentOffice: "Old Office",
        ComputerChanged: computerChanged,
        CurrentDeviceName: "OLD-PC",
        NewDeviceName: "NEW-PC",
        SuUsername: "suadmin");

    private const string DoeJaneDn = "CN=Doe\\, Jane,OU=X,DC=contoso,DC=local";

    private static FakeAdService MakeAdWithUserAndSu()
    {
        var ad = new FakeAdService();
        ad.Users["doej"] = new AdUserInfo("doej", DoeJaneDn, "Jane", "Doe", true, "CN=OldBoss,DC=x", "Old role", "Old Office", "");
        ad.Users["suadmin"] = new AdUserInfo("suadmin", "CN=SU Admin,DC=x", "SU", "Admin", true, null, "", "", "");
        return ad;
    }

    [Fact]
    public void ApplyChanges_NothingChanged_MakesNoAdCallsAndStillAppendsNote()
    {
        var ad = MakeAdWithUserAndSu();
        var service = new TransferService(ad);
        var result = service.ApplyChanges(MakeRequest());

        Assert.Empty(result.Outcomes);
        Assert.True(result.NoteAppended);
        Assert.Contains("Transferred by SU Admin (suadmin) - Ticket # TCK-2001. Changed: ", ad.Users["doej"].Info);
        // 12-hour clock with AM/PM, e.g. "[9/15/2026 3:45 PM]" - Francois asked for this over a
        // 24-hour timestamp on 2026-09-15.
        Assert.Matches(@"^\[\d{1,2}/\d{1,2}/\d{4} \d{1,2}:\d{2} (AM|PM)\]", ad.Users["doej"].Info);
    }

    [Fact]
    public void ApplyChanges_HoneytokenAccount_IsRefusedAndAuditedCritical()
    {
        var ad = MakeAdWithUserAndSu();
        var audit = new RecordingSecurityAudit();
        var service = new TransferService(ad, audit: audit, honeytokenUsers: ["doej"]);

        var result = service.ApplyChanges(MakeRequest(managerChanged: true));

        Assert.Equal("Honeytoken", result.Outcomes.Single().FieldName);
        Assert.False(result.Outcomes.Single().Success);
        Assert.Contains(audit.Events, e => e.Type == SecurityEventType.HoneytokenTripped && e.Severity == SecuritySeverity.Critical);
        // Nothing was actually changed.
        Assert.Equal("CN=OldBoss,DC=x", ad.Users["doej"].ManagerDistinguishedName);
    }

    [Fact]
    public void ApplyChanges_OuOutsideAllowedSubtree_IsRefusedAndAudited()
    {
        var ad = MakeAdWithUserAndSu();
        var audit = new RecordingSecurityAudit();
        var service = new TransferService(ad, audit: audit);

        var result = service.ApplyChanges(MakeRequest(ouChanged: true, newOuDn: "OU=Admins,DC=contoso,DC=local"));

        Assert.False(result.Outcomes.Single(o => o.FieldName == "OU").Success);
        Assert.Contains(audit.Events, e => e.Type == SecurityEventType.OuPolicyBlocked);
    }

    [Fact]
    public void ApplyChanges_ManagerChanged_CallsSetUserManager_AndRecordsSuccess()
    {
        var ad = MakeAdWithUserAndSu();
        var service = new TransferService(ad);
        var result = service.ApplyChanges(MakeRequest(managerChanged: true));

        Assert.Equal("CN=Boss,OU=X,DC=contoso,DC=local", ad.Users["doej"].ManagerDistinguishedName);
        Assert.True(result.Outcomes.Single(o => o.FieldName == "Manager").Success);
        Assert.Contains("Manager: Old Boss (oldboss) -> Boss Person (bossp)", result.ChangeSummaryLines);
    }

    [Fact]
    public void ApplyChanges_OuChanged_ToAllowedOu_CallsMoveUser()
    {
        var ad = MakeAdWithUserAndSu();
        var service = new TransferService(ad);
        var result = service.ApplyChanges(MakeRequest(ouChanged: true));

        Assert.True(result.Outcomes.Single(o => o.FieldName == "OU").Success);
        Assert.Contains("-MOVED-TO-", ad.Users["doej"].DistinguishedName);
    }

    [Fact]
    public void ApplyChanges_OuChanged_ToDisallowedOu_RefusesWithoutCallingMoveUser()
    {
        var ad = MakeAdWithUserAndSu();
        var service = new TransferService(ad);
        // The base OU itself - never a valid target.
        var result = service.ApplyChanges(MakeRequest(ouChanged: true, newOuDn: OuPolicy.AllowedUserOuBaseDn));

        var outcome = result.Outcomes.Single(o => o.FieldName == "OU");
        Assert.False(outcome.Success);
        Assert.Contains("not an allowed OU", outcome.Detail);
        // DN unchanged - Move was never attempted.
        Assert.Equal(DoeJaneDn, ad.Users["doej"].DistinguishedName);
    }

    [Fact]
    public void ApplyChanges_DescriptionAndOfficeChanged_BothApplied()
    {
        var ad = MakeAdWithUserAndSu();
        var service = new TransferService(ad);
        var result = service.ApplyChanges(MakeRequest(descriptionChanged: true, officeChanged: true));

        Assert.Equal("New role", ad.Users["doej"].Description);
        Assert.Equal("New Office", ad.Users["doej"].Office);
        Assert.True(result.Outcomes.Single(o => o.FieldName == "Description").Success);
        Assert.True(result.Outcomes.Single(o => o.FieldName == "Office").Success);
    }

    [Fact]
    public void ApplyChanges_ComputerChanged_AddsSummaryLine_ButMakesNoAdCallItself()
    {
        // TransferService never touches computers directly - ComputerIdentityHandoff does that
        // separately - but the summary/Notes entry must still mention it.
        var ad = MakeAdWithUserAndSu();
        var service = new TransferService(ad);
        var result = service.ApplyChanges(MakeRequest(computerChanged: true));

        Assert.Contains("Computer: OLD-PC -> NEW-PC", result.ChangeSummaryLines);
        Assert.DoesNotContain(result.Outcomes, o => o.FieldName == "Computer");
    }

    [Fact]
    public void ApplyChanges_NoAdUser_SkipsNoteAppend()
    {
        var ad = new FakeAdService(); // "doej" not registered at all
        var service = new TransferService(ad);
        var result = service.ApplyChanges(MakeRequest(hasAdUser: false, computerChanged: true));

        Assert.False(result.NoteAppended);
    }

    [Fact]
    public void ApplyChanges_OneFieldFails_OthersStillApplied()
    {
        var ad = MakeAdWithUserAndSu();
        var service = new TransferService(ad);
        // Force the OU move to fail by pointing DistinguishedName resolution at a nonexistent
        // user for MoveUser only - simplest way here is to make the OU disallowed (already
        // covered above) while manager/description are real changes on a real user.
        var result = service.ApplyChanges(MakeRequest(managerChanged: true, ouChanged: true, newOuDn: OuPolicy.AllowedUserOuBaseDn, descriptionChanged: true));

        Assert.True(result.Outcomes.Single(o => o.FieldName == "Manager").Success);
        Assert.False(result.Outcomes.Single(o => o.FieldName == "OU").Success);
        Assert.True(result.Outcomes.Single(o => o.FieldName == "Description").Success);
    }
}
