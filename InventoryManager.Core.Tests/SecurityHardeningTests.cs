using InventoryManager.Core;
using InventoryManager.Core.Models;
using InventoryManager.Core.Offboarding;

namespace InventoryManager.Core.Tests;

/// <summary>
/// Regression tests for the 2026-09-15 adversarial-review hardening: CSV/DDE formula injection
/// (CWE-1236) in every exporter, and the export -> import round-trip that keeps that
/// neutralization lossless for this app's own files.
/// </summary>
public class CsvFormulaInjectionTests
{
    [Theory]
    [InlineData("=cmd|'/c calc'!A1")]
    [InlineData("+1+1")]
    [InlineData("-2+3")]
    [InlineData("@SUM(1)")]
    [InlineData("\tTabLed")]
    [InlineData("\rCarriage")]
    public void WriteRow_FormulaTriggerCell_IsPrefixedWithApostrophe(string dangerous)
    {
        var written = Csv.WriteRow([dangerous]);
        var firstContentChar = written.StartsWith('"') ? written[1] : written[0];
        Assert.Equal('\'', firstContentChar);
    }

    [Fact]
    public void WriteRow_SafeCell_IsUnchanged()
    {
        Assert.Equal("LAPTOP-01,Jane", Csv.WriteRow(["LAPTOP-01", "Jane"]));
    }

    [Theory]
    [InlineData("=1+1")]
    [InlineData("-lead")]
    [InlineData("@at")]
    public void NeutralizeThenStrip_RoundTripsExactly(string original)
    {
        Assert.Equal(original, Csv.StripFormulaGuard(Csv.NeutralizeFormula(original)));
    }

    [Fact]
    public void InventoryExport_MaliciousDeviceName_IsNeutralized_AndReImportsCleanly()
    {
        var item = new InventoryItem { Id = 1, Status = "Current" };
        item.DeviceName = "=cmd|'/c calc.exe'!A1";
        var csv = InventoryCsvExporter.Export([item]);

        Assert.Contains("'=cmd", csv);
        Assert.DoesNotContain(",=cmd", csv);
        Assert.DoesNotContain("\n=cmd", csv);

        var reimported = InventoryCsvImporter.Import(csv, "Current", () => 1);
        Assert.Equal("=cmd|'/c calc.exe'!A1", reimported.Added[0].DeviceName);
    }

    [Fact]
    public void GroupMembershipCsv_MaliciousSam_IsNeutralized_NormalDnUnchanged()
    {
        // A real group DN always starts with a letter ("CN="/"OU="), so it can never itself be a
        // formula - the injectable column is User (sAMAccountName). Neutralizing it anyway is
        // defense-in-depth against a maliciously named directory object.
        var csv = GroupMembershipCsvWriter.BuildCsv("=cmd|'/c calc'!A1", ["CN=Domain Admins,OU=x,DC=y"]);
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("User,GroupDN", lines[0]);
        Assert.StartsWith("'=cmd", lines[1]);
        Assert.EndsWith("\"CN=Domain Admins,OU=x,DC=y\"", lines[1]);
    }
}
