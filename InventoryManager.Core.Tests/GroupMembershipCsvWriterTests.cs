using InventoryManager.Core.Offboarding;

namespace InventoryManager.Core.Tests;

public class GroupMembershipCsvWriterTests
{
    [Fact]
    public void BuildCsv_HasHeaderAndOneRowPerGroup()
    {
        var csv = GroupMembershipCsvWriter.BuildCsv("smithj", ["CN=Group1,DC=x", "CN=Group2,DC=x"]);
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        // A DN always has multiple comma-separated RDN components, so per CSV/RFC 4180 rules -
        // and matching real Export-Csv behavior - the GroupDN field is always quoted.
        Assert.Equal("User,GroupDN", lines[0]);
        Assert.Equal("smithj,\"CN=Group1,DC=x\"", lines[1]);
        Assert.Equal("smithj,\"CN=Group2,DC=x\"", lines[2]);
    }

    [Fact]
    public void BuildCsv_NoGroups_HeaderOnly()
    {
        var csv = GroupMembershipCsvWriter.BuildCsv("smithj", []);
        Assert.Equal("User,GroupDN\r\n", csv);
    }

    [Fact]
    public void BuildCsv_DnWithComma_IsQuotedAndEscaped()
    {
        // A "Last, First" CN produces exactly this shape in a real DN.
        var csv = GroupMembershipCsvWriter.BuildCsv("smithj", [@"CN=Smith\, John,OU=X,DC=x"]);
        Assert.Contains("\"CN=Smith\\, John,OU=X,DC=x\"", csv);
    }
}
