using InventoryManager.Core.Ad;

namespace InventoryManager.Core.Tests;

public class AccountNamingTests
{
    [Theory]
    [InlineData("Smith", "John", "smithj")]
    [InlineData("O'Brien", "Mary", "obrienm")]
    [InlineData("Smith-Jones", "Anne", "smithjonesa")]
    [InlineData("de la Cruz", "Jose", "delacruzj")]
    public void SanitizeBase_StripsNonAlphaNumeric_AndLowercases(string last, string first, string expected)
    {
        Assert.Equal(expected, AccountNaming.SanitizeBase(last, first));
    }

    [Fact]
    public void SanitizeBase_EmptyFirstName_UsesNoInitial()
    {
        Assert.Equal("smith", AccountNaming.SanitizeBase("Smith", ""));
    }

    [Fact]
    public void GenerateUnique_NoCollision_ReturnsBaseNameUnchanged()
    {
        var result = AccountNaming.GenerateUnique("smithj", _ => false);
        Assert.Equal("smithj", result);
    }

    [Fact]
    public void GenerateUnique_OneCollision_AppendsTwo()
    {
        var result = AccountNaming.GenerateUnique("smithj", name => name == "smithj");
        Assert.Equal("smithj2", result);
    }

    [Fact]
    public void GenerateUnique_MultipleCollisions_IncrementsUntilFree()
    {
        var taken = new HashSet<string> { "smithj", "smithj2", "smithj3", "smithj4" };
        var result = AccountNaming.GenerateUnique("smithj", taken.Contains);
        Assert.Equal("smithj5", result);
    }
}
