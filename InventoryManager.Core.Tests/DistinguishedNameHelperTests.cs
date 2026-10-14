using InventoryManager.Core.Ad;

namespace InventoryManager.Core.Tests;

public class DistinguishedNameHelperTests
{
    [Fact]
    public void GetParentOuDn_SimpleCn_ReturnsEverythingAfterIt()
    {
        Assert.Equal(
            "OU=Main Office,OU=Domain Users,DC=contoso,DC=local",
            DistinguishedNameHelper.GetParentOuDn("CN=Jonathan Wilson,OU=Main Office,OU=Domain Users,DC=contoso,DC=local"));
    }

    [Fact]
    public void GetParentOuDn_CnWithEscapedComma_DoesNotSplitInsideTheName()
    {
        // Real case confirmed live against production AD: a "Last, First" display name comes
        // back from AD with the comma backslash-escaped - a naive split on the first comma would
        // land here, mid-name, instead of at the real OU boundary.
        Assert.Equal(
            "OU=Main Office,OU=Domain Users,DC=contoso,DC=local",
            DistinguishedNameHelper.GetParentOuDn(@"CN=Wilson\, Jonathan,OU=Main Office,OU=Domain Users,DC=contoso,DC=local"));
    }

    [Fact]
    public void GetParentOuDn_NoCnPrefix_ReturnsInputUnchanged()
    {
        Assert.Equal("OU=Something,DC=x", DistinguishedNameHelper.GetParentOuDn("OU=Something,DC=x"));
    }

    [Fact]
    public void GetParentOuDn_EmptyOrNull_ReturnsEmptyOrNullSafely()
    {
        Assert.Equal("", DistinguishedNameHelper.GetParentOuDn(""));
        Assert.Equal("", DistinguishedNameHelper.GetParentOuDn(null!));
    }
}
