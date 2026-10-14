using InventoryManager.Core;
using InventoryManager.Core.Models;

namespace InventoryManager.Core.Tests;

public class InventoryCsvExporterTests
{
    [Fact]
    public void Export_HeaderRowUsesFieldKeys()
    {
        var csv = InventoryCsvExporter.Export([]);
        var header = Csv.Parse(csv)[0];
        Assert.Contains("DeviceName", header);
        Assert.Contains("SerialNumber", header);
        Assert.DoesNotContain("Id", header);
    }

    [Fact]
    public void Export_OneItem_RoundTripsThroughCsv()
    {
        var item = new InventoryItem { Id = 1, Status = "Current", DeviceName = "DEV-1", FirstName = "Jane", LastName = "Doe" };
        item["Description"] = "Has, a comma";
        var csv = InventoryCsvExporter.Export([item]);
        var rows = Csv.Parse(csv);
        var header = rows[0];
        var dataRow = rows[1];
        Assert.Equal("DEV-1", dataRow[Array.IndexOf(header, "DeviceName")]);
        Assert.Equal("Has, a comma", dataRow[Array.IndexOf(header, "Description")]);
    }
}

public class InventoryCsvImporterTests
{
    private static int _nextId = 1;
    private static Func<int> NextId => () => _nextId++;

    public InventoryCsvImporterTests() => _nextId = 1;

    [Fact]
    public void Import_MatchesColumnsByKey()
    {
        var csv = "DeviceName,FirstName,LastName\r\nDEV-1,Jane,Doe\r\n";
        var result = InventoryCsvImporter.Import(csv, "Current", NextId);
        Assert.Single(result.Added);
        Assert.Equal("DEV-1", result.Added[0].DeviceName);
        Assert.Equal("Jane", result.Added[0].FirstName);
    }

    [Fact]
    public void Import_MatchesColumnsByLabel()
    {
        var csv = "Device Name,Serial #\r\nDEV-1,SN123\r\n";
        var result = InventoryCsvImporter.Import(csv, "Current", NextId);
        Assert.Equal("SN123", result.Added[0]["SerialNumber"]);
    }

    [Fact]
    public void Import_MatchesColumnsByAlias()
    {
        var csv = "Device,Serial\r\nDEV-1,SN123\r\n";
        var result = InventoryCsvImporter.Import(csv, "Current", NextId);
        Assert.Equal("DEV-1", result.Added[0].DeviceName);
        Assert.Equal("SN123", result.Added[0]["SerialNumber"]);
    }

    [Fact]
    public void Import_BlankDeviceNameButHasName_UsesNoPcPlaceholder()
    {
        var csv = "DeviceName,LastName,FirstName\r\n,Doe,Jane\r\n";
        var result = InventoryCsvImporter.Import(csv, "Current", NextId);
        Assert.Single(result.Added);
        Assert.Equal("No PC", result.Added[0].DeviceName);
    }

    [Fact]
    public void Import_CompletelyBlankRow_IsSkipped()
    {
        var csv = "DeviceName,LastName,FirstName\r\n,,\r\n";
        var result = InventoryCsvImporter.Import(csv, "Current", NextId);
        Assert.Empty(result.Added);
        Assert.Equal(1, result.Skipped);
    }

    [Fact]
    public void Import_InvalidDropdownValue_FallsBackToDefault()
    {
        var csv = "DeviceName,Domain\r\nDEV-1,NotARealOption\r\n";
        var result = InventoryCsvImporter.Import(csv, "Current", NextId);
        Assert.Equal("CONTOSO", result.Added[0]["Domain"]);
    }

    [Fact]
    public void Import_ValidDropdownValue_IsKept()
    {
        var csv = "DeviceName,Domain\r\nDEV-1,Off-Domain\r\n";
        var result = InventoryCsvImporter.Import(csv, "Current", NextId);
        Assert.Equal("Off-Domain", result.Added[0]["Domain"]);
    }

    [Fact]
    public void Import_AssignsSequentialIdsFromNextIdFunc()
    {
        var csv = "DeviceName\r\nDEV-1\r\nDEV-2\r\n";
        var result = InventoryCsvImporter.Import(csv, "Current", NextId);
        Assert.Equal(1, result.Added[0].Id);
        Assert.Equal(2, result.Added[1].Id);
    }

    [Fact]
    public void Import_EmptyCsv_ReturnsNoRows()
    {
        var result = InventoryCsvImporter.Import("", "Current", NextId);
        Assert.Empty(result.Added);
        Assert.Equal(0, result.Skipped);
    }

    [Fact]
    public void Import_QuotedFieldWithComma_ParsesCorrectly()
    {
        var csv = "DeviceName,Description\r\nDEV-1,\"Smith, Jane\"\r\n";
        var result = InventoryCsvImporter.Import(csv, "Current", NextId);
        Assert.Equal("Smith, Jane", result.Added[0]["Description"]);
    }

    [Fact]
    public void Import_TargetStatus_IsAppliedToEveryAddedRow()
    {
        var csv = "DeviceName\r\nDEV-1\r\n";
        var result = InventoryCsvImporter.Import(csv, "ITStock", NextId);
        Assert.Equal("ITStock", result.Added[0].Status);
    }
}
