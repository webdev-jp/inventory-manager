using InventoryManager.Core.Ad;

namespace InventoryManager.Core.Tests;

public class PrivilegedGroupPolicyTests
{
    private const string DomainSid = "S-1-5-21-1111111111-2222222222-3333333333";

    [Theory]
    [InlineData("DnsAdmins")]
    [InlineData("Cert Publishers")]
    [InlineData("dnsadmins")] // name match is case-insensitive
    public void IsPrivileged_NameBasedGroups_AreAlwaysPrivileged_RegardlessOfSid(string groupName)
    {
        Assert.True(PrivilegedGroupPolicy.IsPrivileged(groupName, groupSid: "S-1-5-21-9-9-9-9999", domainSid: DomainSid));
    }

    [Theory]
    [InlineData(512)] // Domain Admins
    [InlineData(518)] // Schema Admins
    [InlineData(519)] // Enterprise Admins
    [InlineData(520)] // Group Policy Creator Owners
    [InlineData(526)] // Key Admins
    [InlineData(527)] // Enterprise Key Admins
    public void IsPrivileged_DomainRidGroups_AreDetectedBySid(int rid)
    {
        var sid = $"{DomainSid}-{rid}";
        Assert.True(PrivilegedGroupPolicy.IsPrivileged("SomeRenamedGroup", sid, DomainSid));
    }

    [Theory]
    [InlineData(544)] // Administrators
    [InlineData(548)] // Account Operators
    [InlineData(549)] // Server Operators
    [InlineData(550)] // Print Operators
    [InlineData(551)] // Backup Operators
    public void IsPrivileged_BuiltinRidGroups_AreDetectedBySid_EvenWithoutDomainSid(int rid)
    {
        var sid = $"S-1-5-32-{rid}";
        // BUILTIN groups use a fixed form identical across every domain - must be detected even
        // if the domain SID lookup itself failed (domainSid null).
        Assert.True(PrivilegedGroupPolicy.IsPrivileged("SomeRenamedGroup", sid, domainSid: null));
    }

    [Theory]
    [InlineData("Organization Management")]
    [InlineData("Exchange Trusted Subsystem")]
    [InlineData("Exchange Windows Permissions")]
    public void IsPrivileged_WellKnownRidlessEscalationGroups_AreNameMatched(string groupName)
    {
        // These have no fixed well-known RID (their SID is allocated when the role is installed),
        // so they can only be caught by name - each is a classic pivot to Domain Admin.
        Assert.True(PrivilegedGroupPolicy.IsPrivileged(groupName, groupSid: "S-1-5-21-9-9-9-4242", domainSid: DomainSid));
    }

    [Fact]
    public void IsPrivileged_OrdinaryGroup_IsNotPrivileged()
    {
        Assert.False(PrivilegedGroupPolicy.IsPrivileged("Marketing Team", $"{DomainSid}-5001", DomainSid));
    }

    [Fact]
    public void IsPrivileged_UnresolvableSid_FailsClosed()
    {
        // The group couldn't be resolved in AD at all - this method's job is to gate a real AD
        // write, so an unknown group must never be treated as safe.
        Assert.True(PrivilegedGroupPolicy.IsPrivileged("SomeGroup", groupSid: null, domainSid: DomainSid));
    }
}
