using InventoryManager.Core.Ad;

namespace InventoryManager.Core.Tests;

public class ProtectedAccountPolicyTests
{
    // Account names modeled on a real incident (anonymized).
    private static readonly string[] AllAccounts =
        ["jdoe", "sujdoe", "keyesM", "sukeyesM", "SuttonV", "SummersA", "teamsadmin", "SPAdmin",
         "Administrator", "OrgAdmin", "dmorgan", "TestUserA", "calendars"];

    private static ProtectedAccountPolicy Policy(params string[] privileged) =>
        ProtectedAccountPolicy.Create(privileged, AllAccounts);

    [Theory]
    [InlineData("teamsadmin")]
    [InlineData("SPAdmin")]
    [InlineData("Administrator")]
    [InlineData("OrgAdmin")]
    [InlineData("sujdoe")]
    [InlineData("sujdoe")]
    [InlineData("sukeyesM")]
    public void AdminStyleNames_AreProtected(string sam)
    {
        Assert.True(Policy().IsProtected(sam));
    }

    [Theory]
    [InlineData("SuttonV")]   // starts with "su", but "arezV" isn't an account
    [InlineData("SummersA")]
    [InlineData("TestUserA")]
    [InlineData("calendars")]
    [InlineData("jdoe")]
    public void OrdinaryAccounts_AreNotProtected(string sam)
    {
        Assert.False(Policy().IsProtected(sam));
    }

    [Fact]
    public void PrivilegedGroupMember_IsProtected_EvenWithOrdinaryName()
    {
        var policy = Policy("dmorgan");

        Assert.Equal("member of a privileged AD group", policy.GetProtectionReason("DMORGAN"));
    }

    [Fact]
    public void BlankUsername_IsNotProtected()
    {
        Assert.Null(Policy().GetProtectionReason(""));
        Assert.Null(Policy().GetProtectionReason(null));
    }

    [Fact]
    public void FailClosed_ProtectsEveryAccount()
    {
        var policy = ProtectedAccountPolicy.FailClosed("AD down");

        Assert.True(policy.IsProtected("TestUserA"));
        Assert.Equal("AD down", policy.GetProtectionReason("calendars"));
    }

    [Fact]
    public void Load_UsesAdPrivilegedListAndAccountList()
    {
        var ad = new FakeAdService();
        ad.PrivilegedUsers.Add("dmorgan");
        ad.AllUsers.Add(new AdUserMatchInfo("jdoe", "Jean", "Francois", true));
        ad.AllUsers.Add(new AdUserMatchInfo("sujdoe", "", "", true));

        var policy = ProtectedAccountPolicy.Load(ad, ad.DomainSid);

        Assert.True(policy.IsProtected("dmorgan"));
        Assert.True(policy.IsProtected("sujdoe"));
        Assert.False(policy.IsProtected("jdoe"));
    }

    [Fact]
    public void Load_ThrowsWhenAdUnavailable_SoCallerCanFailClosed()
    {
        var ad = new FakeAdService { FailPrivilegedLookup = true };

        Assert.Throws<InvalidOperationException>(() => ProtectedAccountPolicy.Load(ad, ad.DomainSid));
    }
}
