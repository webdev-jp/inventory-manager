using InventoryManager.Core.Ad;

namespace InventoryManager.Core.Tests;

public class OuPolicyTests
{
    [Fact]
    public void IsAllowedForNewUsers_SubOu_IsAllowed()
    {
        Assert.True(OuPolicy.IsAllowedForNewUsers("OU=Main Office,OU=Domain Users,DC=contoso,DC=local"));
    }

    [Fact]
    public void IsAllowedForNewUsers_DeeplyNestedSubOu_IsAllowed()
    {
        Assert.True(OuPolicy.IsAllowedForNewUsers("OU=Clinical,OU=Main Office,OU=Domain Users,DC=contoso,DC=local"));
    }

    [Fact]
    public void IsAllowedForNewUsers_BaseOuItself_IsNotAllowed()
    {
        // Users may never be created directly in "Domain Users" - only in a sub-OU of it.
        Assert.False(OuPolicy.IsAllowedForNewUsers("OU=Domain Users,DC=contoso,DC=local"));
    }

    [Fact]
    public void IsAllowedForNewUsers_UnrelatedOu_IsNotAllowed()
    {
        Assert.False(OuPolicy.IsAllowedForNewUsers("OU=Domain Computers,DC=contoso,DC=local"));
    }

    [Fact]
    public void IsAllowedForNewUsers_SimilarButDistinctSuffix_IsNotAllowed()
    {
        // A naive EndsWith on the raw base string (without the leading comma) would wrongly
        // match "OU=Not Domain Users,DC=..." - confirms the comma-prefixed boundary check works.
        Assert.False(OuPolicy.IsAllowedForNewUsers("OU=Not Domain Users,DC=contoso,DC=local"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsAllowedForNewUsers_BlankDn_IsNotAllowed(string? dn)
    {
        Assert.False(OuPolicy.IsAllowedForNewUsers(dn));
    }

    [Fact]
    public void IsAllowedForNewUsers_IsCaseInsensitive()
    {
        Assert.True(OuPolicy.IsAllowedForNewUsers("ou=Main Office,ou=domain users,DC=contoso,dc=local"));
    }
}
