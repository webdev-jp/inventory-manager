using InventoryManager.Core;

namespace InventoryManager.Core.Tests;

public class CsvTests
{
    [Fact]
    public void WriteRow_PlainFields_NoQuoting()
    {
        Assert.Equal("a,b,c", Csv.WriteRow(["a", "b", "c"]));
    }

    [Fact]
    public void WriteRow_FieldWithComma_IsQuoted()
    {
        Assert.Equal("\"a,b\",c", Csv.WriteRow(["a,b", "c"]));
    }

    [Fact]
    public void WriteRow_FieldWithQuote_IsQuotedAndDoubled()
    {
        Assert.Equal("\"say \"\"hi\"\"\"", Csv.WriteRow(["say \"hi\""]));
    }

    [Fact]
    public void Parse_SimpleTwoRowCsv()
    {
        var rows = Csv.Parse("a,b,c\r\n1,2,3\r\n");
        Assert.Equal(2, rows.Count);
        Assert.Equal(["a", "b", "c"], rows[0]);
        Assert.Equal(["1", "2", "3"], rows[1]);
    }

    [Fact]
    public void Parse_QuotedFieldWithEmbeddedComma()
    {
        var rows = Csv.Parse("Name,Note\r\nJane,\"Smith, Jane\"\r\n");
        Assert.Equal("Smith, Jane", rows[1][1]);
    }

    [Fact]
    public void Parse_QuotedFieldWithEmbeddedNewline()
    {
        var rows = Csv.Parse("Note\r\n\"line1\nline2\"\r\n");
        Assert.Equal(2, rows.Count);
        Assert.Equal("line1\nline2", rows[1][0]);
    }

    [Fact]
    public void Parse_DoubledQuoteInsideQuotedField()
    {
        var rows = Csv.Parse("Note\r\n\"she said \"\"hi\"\"\"\r\n");
        Assert.Equal("she said \"hi\"", rows[1][0]);
    }

    [Fact]
    public void Parse_NoTrailingNewline_StillParsesLastRow()
    {
        var rows = Csv.Parse("a,b\r\n1,2");
        Assert.Equal(2, rows.Count);
        Assert.Equal(["1", "2"], rows[1]);
    }

    [Fact]
    public void RoundTrip_WriteThenParse_PreservesValues()
    {
        var written = Csv.WriteRow(["has,comma", "has\"quote", "plain", "has\r\nnewline"]) + "\r\n";
        var parsed = Csv.Parse(written);
        Assert.Equal(["has,comma", "has\"quote", "plain", "has\r\nnewline"], parsed[0]);
    }
}
