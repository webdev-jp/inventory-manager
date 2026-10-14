using InventoryManager.Core.Ad;
using InventoryManager.Core.Permissions;
using InventoryManager.Core.Security;

namespace InventoryManager.Core.Tests;

public class PermissionCopyTests
{
    private static readonly AdUserInfo Source = new("srcu", "CN=Source User,OU=X,DC=test,DC=local", "Source", "User", true, null, "", "", "");
    private static readonly AdUserInfo Target = new("tgtu", "CN=Target User,OU=X,DC=test,DC=local", "Target", "User", true, null, "", "", "");

    private static PermissionCopyContext Ctx(string su = "suadmin") => new(Source, Target, su);

    private static FakeAdService MakeAd()
    {
        var ad = new FakeAdService();
        ad.Users["srcu"] = Source;
        ad.Users["tgtu"] = Target;
        ad.Users["suadmin"] = new AdUserInfo("suadmin", "CN=SU Admin,DC=x", "SU", "Admin", true, null, "", "", "");
        ad.GroupDnsByUser["srcu"] = [];
        ad.GroupDnsByUser["tgtu"] = [];
        return ad;
    }

    private static (PermissionCopyService Service, AdGroupPermissionProvider Provider) MakeService(
        FakeAdService ad, RecordingSecurityAudit? audit = null, IReadOnlyCollection<string>? granters = null,
        IReadOnlyCollection<string>? honeytokenGroups = null, IReadOnlyCollection<string>? honeytokenUsers = null)
    {
        var provider = new AdGroupPermissionProvider(ad, granters ?? [], audit, honeytokenGroups ?? []);
        return (new PermissionCopyService(ad, [provider], audit, honeytokenUsers ?? []), provider);
    }

    [Fact]
    public void Scan_ReportsSetDifference_AndAlreadyHas()
    {
        var ad = MakeAd();
        var hr = ad.AddGroup("HR Staff", "S-1-5-21-1-2-3-2001");
        var vpn = ad.AddGroup("VPN Users", "S-1-5-21-1-2-3-2002");
        ad.GroupDnsByUser["srcu"].AddRange([hr, vpn]);
        ad.GroupDnsByUser["tgtu"].Add(vpn);
        var (service, _) = MakeService(ad);

        var scan = service.Scan(Ctx(), null, CancellationToken.None);

        Assert.Empty(scan.Errors);
        Assert.Equal(PermissionItemStatus.ToAdd, scan.Items.Single(i => i.DisplayName == "HR Staff").Status);
        var already = scan.Items.Single(i => i.DisplayName == "VPN Users");
        Assert.Equal(PermissionItemStatus.AlreadyHas, already.Status);
        Assert.False(already.DefaultChecked);
    }

    [Fact]
    public void Scan_PrivilegedGroup_IsBlocked_WithoutAuditing()
    {
        var ad = MakeAd();
        var da = ad.AddGroup("Domain Admins", "S-1-5-21-1-2-3-512");
        ad.GroupDnsByUser["srcu"].Add(da);
        var audit = new RecordingSecurityAudit();
        var (service, _) = MakeService(ad, audit);

        var item = service.Scan(Ctx(), null, CancellationToken.None).Items.Single();

        Assert.Equal(PermissionItemStatus.Blocked, item.Status);
        Assert.Contains("privileged", item.StatusReason);
        Assert.Empty(audit.Events); // a scan is not an attempt
    }

    [Fact]
    public void Scan_GroupNestedIntoPrivileged_IsBlocked()
    {
        var ad = MakeAd();
        var helpdesk = ad.AddGroup("Helpdesk Tier2", "S-1-5-21-1-2-3-3000");
        ad.GroupParentSids["Helpdesk Tier2"] = ["S-1-5-21-1-2-3-512"];
        ad.GroupDnsByUser["srcu"].Add(helpdesk);
        var (service, _) = MakeService(ad);

        Assert.Equal(PermissionItemStatus.Blocked, service.Scan(Ctx(), null, CancellationToken.None).Items.Single().Status);
    }

    [Fact]
    public void Scan_PrivilegedGroup_IsAllowedForSuperSu()
    {
        var ad = MakeAd();
        ad.GroupDnsByUser["srcu"].Add(ad.AddGroup("Domain Admins", "S-1-5-21-1-2-3-512"));
        var (service, _) = MakeService(ad, granters: ["suadmin"]);

        Assert.Equal(PermissionItemStatus.ToAdd, service.Scan(Ctx(), null, CancellationToken.None).Items.Single().Status);
    }

    [Fact]
    public void Scan_UnreadableGroup_IsBlockedNotThrown()
    {
        var ad = MakeAd();
        ad.GroupDnsByUser["srcu"].Add("CN=Ghost\\, Group,OU=Groups,DC=test,DC=local");
        var (service, _) = MakeService(ad);

        var item = service.Scan(Ctx(), null, CancellationToken.None).Items.Single();
        Assert.Equal(PermissionItemStatus.Blocked, item.Status);
        Assert.Equal("Ghost, Group", item.DisplayName);
    }

    [Fact]
    public void Apply_AddsByDn_WritesNote_AndAudits()
    {
        var ad = MakeAd();
        var hr = ad.AddGroup("HR Staff", "S-1-5-21-1-2-3-2001");
        ad.GroupDnsByUser["srcu"].Add(hr);
        var audit = new RecordingSecurityAudit();
        var (service, _) = MakeService(ad, audit);
        var scan = service.Scan(Ctx(), null, CancellationToken.None);

        var report = service.Apply(Ctx(), scan.Items, null, CancellationToken.None);

        Assert.Equal(1, report.Succeeded);
        Assert.Contains(("tgtu", hr), ad.AddToGroupByDnCalls);
        Assert.Contains("Permissions copied from Source User (srcu)", ad.Users["tgtu"].Info);
        Assert.Contains(audit.Events, e => e.Type == SecurityEventType.PermissionsCopied && e.Target == "tgtu");
        Assert.Empty(ad.AddToGroupByDnCalls.Where(c => c.Identity == "srcu")); // never touches the source
    }

    [Fact]
    public void Apply_RefusesBlockedAndAlreadyHasItems_EvenIfPassedIn()
    {
        var ad = MakeAd();
        var da = ad.AddGroup("Domain Admins", "S-1-5-21-1-2-3-512");
        var vpn = ad.AddGroup("VPN Users", "S-1-5-21-1-2-3-2002");
        ad.GroupDnsByUser["srcu"].AddRange([da, vpn]);
        ad.GroupDnsByUser["tgtu"].Add(vpn);
        var (service, _) = MakeService(ad);
        var scan = service.Scan(Ctx(), null, CancellationToken.None);

        var report = service.Apply(Ctx(), scan.Items, null, CancellationToken.None);

        Assert.Equal(0, report.Succeeded);
        Assert.Empty(ad.AddToGroupByDnCalls);
    }

    [Fact]
    public void Apply_ReChecksPrivilege_EvenForAToAddItem()
    {
        // Group was ordinary at scan time but got nested into Domain Admins before Apply.
        var ad = MakeAd();
        var helpdesk = ad.AddGroup("Helpdesk Tier2", "S-1-5-21-1-2-3-3000");
        ad.GroupDnsByUser["srcu"].Add(helpdesk);
        var audit = new RecordingSecurityAudit();
        var (service, _) = MakeService(ad, audit);
        var scan = service.Scan(Ctx(), null, CancellationToken.None);
        Assert.True(scan.Items.Single().CanApply);

        ad.GroupParentSids["Helpdesk Tier2"] = ["S-1-5-21-1-2-3-512"];
        var report = service.Apply(Ctx(), scan.Items, null, CancellationToken.None);

        Assert.False(report.Results.Single().Success);
        Assert.Empty(ad.AddToGroupByDnCalls);
        Assert.Contains(audit.Events, e => e.Type == SecurityEventType.NestedPrivilegeRefused);
    }

    [Fact]
    public void Apply_PartialFailure_ReportsEachItem_AndContinues()
    {
        var ad = MakeAd();
        var a = ad.AddGroup("Group A", "S-1-5-21-1-2-3-2001");
        var b = ad.AddGroup("Group B", "S-1-5-21-1-2-3-2002");
        ad.GroupDnsByUser["srcu"].AddRange([a, b]);
        ad.GroupDnsThatFailToAdd.Add(a);
        var audit = new RecordingSecurityAudit();
        var (service, _) = MakeService(ad, audit);
        var scan = service.Scan(Ctx(), null, CancellationToken.None);

        var report = service.Apply(Ctx(), scan.Items, null, CancellationToken.None);

        Assert.Equal(1, report.Succeeded);
        Assert.Equal(1, report.Failed);
        Assert.Contains("Simulated failure", report.Results.Single(r => !r.Success).Message);
        Assert.Contains(audit.Events, e => e.Type == SecurityEventType.PermissionsCopied && e.Severity == SecuritySeverity.Warning);
    }

    [Fact]
    public void ProviderFailure_DuringScan_IsReported_NotThrown()
    {
        var ad = MakeAd();
        var service = new PermissionCopyService(ad, [new ThrowingProvider()], honeytokenUsers: []);

        var scan = service.Scan(Ctx(), null, CancellationToken.None);

        Assert.Single(scan.Errors);
        Assert.Equal("Broken", scan.Errors[0].ProviderName);
    }

    [Fact]
    public void SameUser_IsBlocked()
    {
        var ad = MakeAd();
        var (service, _) = MakeService(ad);
        Assert.Throws<PermissionCopyBlockedException>(() =>
            service.Scan(new PermissionCopyContext(Source, Source, "suadmin"), null, CancellationToken.None));
    }

    [Fact]
    public void HoneytokenTarget_IsBlocked_AndAlertedCritical()
    {
        var ad = MakeAd();
        var audit = new RecordingSecurityAudit();
        var (service, _) = MakeService(ad, audit, honeytokenUsers: ["tgtu"]);

        Assert.Throws<PermissionCopyBlockedException>(() => service.Scan(Ctx(), null, CancellationToken.None));
        Assert.Contains(audit.Events, e => e.Type == SecurityEventType.HoneytokenTripped && e.Severity == SecuritySeverity.Critical);
    }

    [Fact]
    public void Cancellation_StopsBetweenItems_AndStillReports()
    {
        var ad = MakeAd();
        ad.GroupDnsByUser["srcu"].AddRange([ad.AddGroup("Group A", "S-1-5-21-1-2-3-2001"), ad.AddGroup("Group B", "S-1-5-21-1-2-3-2002")]);
        var (service, _) = MakeService(ad);
        var scan = service.Scan(Ctx(), null, CancellationToken.None);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var report = service.Apply(Ctx(), scan.Items, null, cts.Token);

        Assert.True(report.Cancelled);
        Assert.Empty(report.Results);
        Assert.Empty(ad.AddToGroupByDnCalls);
    }

    [Fact]
    public void BuildCsv_NeutralizesFormulaInGroupName()
    {
        var item = new PermissionItem("p", PermissionCategory.AdGroup, "=cmd|'/c calc'!A1", "k", "", PermissionItemStatus.ToAdd);
        var report = new PermissionCopyReport(Ctx(), new DateTime(2026, 9, 25), [new PermissionApplyResult(item, true, "Added.")], false);

        var csv = PermissionCopyService.BuildCsv(report);

        Assert.Contains("'=cmd", csv);
        Assert.StartsWith("Timestamp,Source,Target,Category", csv);
    }

    [Fact]
    public void DistinguishedNameHelper_FirstRdn_UnescapesComma()
    {
        Assert.Equal("Doe, Jane", DistinguishedNameHelper.GetFirstRdnValue("CN=Doe\\, Jane,OU=X,DC=y"));
        Assert.Equal("", DistinguishedNameHelper.GetFirstRdnValue(""));
    }

    private sealed class ThrowingProvider : IPermissionProvider
    {
        public string Name => "Broken";
        public IReadOnlyList<PermissionItem> Scan(PermissionCopyContext context, IProgress<string>? progress, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("sign-in expired");
        public PermissionApplyResult Apply(PermissionItem item, PermissionCopyContext context) => throw new NotSupportedException();
    }
}
