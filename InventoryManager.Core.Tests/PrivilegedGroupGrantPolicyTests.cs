using InventoryManager.Core.Ad;

namespace InventoryManager.Core.Tests;

public class PrivilegedGroupGrantPolicyTests
{
    [Fact]
    public void IsAllowed_UsernameOnList_ReturnsTrue()
    {
        Assert.True(PrivilegedGroupGrantPolicy.IsAllowed("supattersonb", ["supattersonb", "sudmorgan"]));
    }

    [Fact]
    public void IsAllowed_CaseInsensitive()
    {
        Assert.True(PrivilegedGroupGrantPolicy.IsAllowed("SUPATTERSONB", ["supattersonb"]));
    }

    [Fact]
    public void IsAllowed_UsernameNotOnList_ReturnsFalse()
    {
        Assert.False(PrivilegedGroupGrantPolicy.IsAllowed("ordinarysu", ["supattersonb", "sudmorgan"]));
    }

    [Fact]
    public void IsAllowed_EmptyList_DeniesEveryone()
    {
        Assert.False(PrivilegedGroupGrantPolicy.IsAllowed("supattersonb", []));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsAllowed_NullOrWhitespaceUsername_ReturnsFalse(string? username)
    {
        Assert.False(PrivilegedGroupGrantPolicy.IsAllowed(username, ["supattersonb"]));
    }

    [Fact]
    public void IsAllowed_NoListPassed_UsesRealProductionList()
    {
        // Pins the two real accounts Francois named (2026-09-15) so an accidental future edit to
        // AllowedPrivilegedGroupGranters is caught either direction - losing a name, or someone
        // else being added without an explicit decision to do so.
        Assert.True(PrivilegedGroupGrantPolicy.IsAllowed("supattersonb"));
        Assert.True(PrivilegedGroupGrantPolicy.IsAllowed("sudmorgan"));
        Assert.False(PrivilegedGroupGrantPolicy.IsAllowed("someone-else"));
    }
}
