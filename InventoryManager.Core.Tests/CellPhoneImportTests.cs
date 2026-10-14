using InventoryManager.Core.CellPhones;
using InventoryManager.Core.Models;

namespace InventoryManager.Core.Tests;

public class CellPhoneCsvParserTests
{
    [Fact]
    public void Parse_FindsColumnsByHeaderName_RegardlessOfOrder()
    {
        var content = "Wireless number,Device model,User name\r\n914-555-0100,IPHONE 11,ROSA DIAZ\r\n";
        var rows = CellPhoneCsvParser.Parse(content);

        var row = Assert.Single(rows);
        Assert.Equal("ROSA DIAZ", row.RawUserName);
        Assert.Equal("914-555-0100", row.WirelessNumber);
        Assert.Equal("IPHONE 11", row.DeviceModel);
    }

    [Fact]
    public void Parse_MissingDeviceModelColumn_LeavesItBlank()
    {
        var content = "User name,Wireless number\r\nJESSY DIAZ,914-555-0100\r\n";
        var rows = CellPhoneCsvParser.Parse(content);

        Assert.Equal("", Assert.Single(rows).DeviceModel);
    }

    [Fact]
    public void Parse_BlankRow_Skipped()
    {
        var content = "User name,Wireless number\r\nJESSY DIAZ,914-555-0100\r\n, \r\n";
        var rows = CellPhoneCsvParser.Parse(content);

        Assert.Single(rows);
    }

    [Fact]
    public void Parse_MissingRequiredColumns_Throws()
    {
        var content = "Account name,Cost center\r\nCONTOSO,1234\r\n";
        Assert.Throws<InvalidOperationException>(() => CellPhoneCsvParser.Parse(content));
    }

    [Fact]
    public void Parse_EmptyContent_ReturnsEmptyList()
    {
        Assert.Empty(CellPhoneCsvParser.Parse(""));
    }
}

public class CellPhoneMatcherTests
{
    private static InventoryItem MakeItem(int id, string firstName, string lastName, string status = "Current")
    {
        return new InventoryItem { Id = id, Status = status, FirstName = firstName, LastName = lastName, DeviceName = $"DEV-{id}" };
    }

    [Fact]
    public void Match_SimpleTwoWordName_MatchesExactly()
    {
        var items = new[] { MakeItem(1, "Rosa", "Diaz") };
        var csv = new[] { new CellPhoneCsvRow("ROSA DIAZ", "914-555-0100", "IPHONE 11") };

        var result = CellPhoneMatcher.Match(csv, items);

        var match = Assert.Single(result.Matched);
        Assert.Equal(1, match.Item.Id);
        Assert.Empty(result.Ambiguous);
        Assert.Empty(result.Unmatched);
    }

    [Fact]
    public void Match_ThreeWordName_FindsCorrectSplitAgainstRealInventoryNames()
    {
        // "Jean Paul" is the real first name, "Francois" the real last name - a naive
        // first-word-only split ("Jean"/"Paul Francois") would miss this; only the
        // two-words-first/one-word-last split matches the real inventory record.
        var items = new[] { MakeItem(1, "Jean Paul", "Francois") };
        var csv = new[] { new CellPhoneCsvRow("JEAN PAUL FRANCOIS", "914-555-0100", "") };

        var result = CellPhoneMatcher.Match(csv, items);

        var match = Assert.Single(result.Matched);
        Assert.Equal(1, match.Item.Id);
    }

    [Fact]
    public void Match_FourWordName_TwoSurnames_FindsCorrectSplit()
    {
        // Hispanic two-given-name/two-surname convention: "Maria Jose" + "Lopez Garcia".
        var items = new[] { MakeItem(1, "Maria Jose", "Lopez Garcia") };
        var csv = new[] { new CellPhoneCsvRow("MARIA JOSE LOPEZ GARCIA", "914-555-0100", "") };

        var result = CellPhoneMatcher.Match(csv, items);

        var match = Assert.Single(result.Matched);
        Assert.Equal(1, match.Item.Id);
    }

    [Fact]
    public void Match_NoInventoryRecordMatches_Unmatched()
    {
        // A shared/department line, not a real person.
        var items = new[] { MakeItem(1, "Rosa", "Diaz") };
        var csv = new[] { new CellPhoneCsvRow("FRONT DESK PHONE", "914-555-0199", "") };

        var result = CellPhoneMatcher.Match(csv, items);

        Assert.Empty(result.Matched);
        Assert.Empty(result.Ambiguous);
        Assert.Single(result.Unmatched);
    }

    [Fact]
    public void Match_SingleWordName_NeverMatches()
    {
        var items = new[] { MakeItem(1, "Rosa", "Diaz") };
        var csv = new[] { new CellPhoneCsvRow("DIAZ", "914-555-0100", "") };

        var result = CellPhoneMatcher.Match(csv, items);

        Assert.Single(result.Unmatched);
    }

    [Fact]
    public void Match_TwoEmployeesShareTheSameName_Ambiguous()
    {
        var items = new[] { MakeItem(1, "John", "Smith"), MakeItem(2, "John", "Smith") };
        var csv = new[] { new CellPhoneCsvRow("JOHN SMITH", "914-555-0100", "") };

        var result = CellPhoneMatcher.Match(csv, items);

        var ambiguity = Assert.Single(result.Ambiguous);
        Assert.Equal(2, ambiguity.Candidates.Count);
    }

    [Fact]
    public void Match_NonCurrentItems_NeverConsidered()
    {
        // Caller is responsible for filtering to Current Inventory before calling Match - this just
        // confirms Match itself has no opinion and matches whatever list it's handed.
        var items = new[] { MakeItem(1, "Rosa", "Diaz", status: "ITStock") };
        var csv = new[] { new CellPhoneCsvRow("ROSA DIAZ", "914-555-0100", "") };

        var result = CellPhoneMatcher.Match(csv, items);

        Assert.Single(result.Matched);
    }

    [Fact]
    public void Apply_SetsCellPhoneAndDeviceModel()
    {
        var item = MakeItem(1, "Rosa", "Diaz");
        var row = new CellPhoneCsvRow("ROSA DIAZ", "914-555-0100", "IPHONE 11");

        CellPhoneMatcher.Apply(item, row);

        Assert.Equal("914-555-0100", item["CellPhone"]);
        Assert.Equal("IPHONE 11", item["DeviceModel"]);
    }

    [Fact]
    public void Apply_BlankCsvModel_LeavesExistingModelUntouched()
    {
        var item = MakeItem(1, "Rosa", "Diaz");
        item["DeviceModel"] = "Old Model";
        var row = new CellPhoneCsvRow("ROSA DIAZ", "914-555-0100", "");

        CellPhoneMatcher.Apply(item, row);

        Assert.Equal("914-555-0100", item["CellPhone"]);
        Assert.Equal("Old Model", item["DeviceModel"]);
    }
}
