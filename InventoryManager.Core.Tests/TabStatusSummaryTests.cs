using InventoryManager.Core;
using InventoryManager.Core.Models;

namespace InventoryManager.Core.Tests;

public class TabStatusSummaryCalculatorTests
{
    private static InventoryItem MakeItem(int id) => new() { Id = id, Status = "Current", DeviceName = $"DEV-{id}" };

    [Fact]
    public void EmptySet_AllZero()
    {
        var summary = TabStatusSummaryCalculator.Compute([]);
        Assert.Equal(0, summary.Total);
        Assert.Equal("0 total, 0 no Serial #, 0 AD-disabled, 0 user not in AD, 0 computer not in AD, 0 computer disabled", summary.ToString());
    }

    [Fact]
    public void MissingSerial_IsCounted()
    {
        var item = MakeItem(1);
        item["SerialNumber"] = "";
        var summary = TabStatusSummaryCalculator.Compute([item]);
        Assert.Equal(1, summary.Total);
        Assert.Equal(1, summary.MissingSerial);
    }

    [Fact]
    public void PresentSerial_NotCounted()
    {
        var item = MakeItem(1);
        item["SerialNumber"] = "SN-1";
        var summary = TabStatusSummaryCalculator.Compute([item]);
        Assert.Equal(0, summary.MissingSerial);
    }

    [Fact]
    public void AdAccountDisabled_IsCountedOnlyUnderThatBucket()
    {
        var item = MakeItem(1);
        item.ADFlagReason = "AD account disabled";
        var summary = TabStatusSummaryCalculator.Compute([item]);
        Assert.Equal(1, summary.AdDisabled);
        Assert.Equal(0, summary.UserNotFound);
    }

    [Fact]
    public void NoMatchingAdUser_IsCountedOnlyUnderThatBucket()
    {
        var item = MakeItem(1);
        item.ADFlagReason = "No matching AD user found";
        var summary = TabStatusSummaryCalculator.Compute([item]);
        Assert.Equal(0, summary.AdDisabled);
        Assert.Equal(1, summary.UserNotFound);
    }

    [Fact]
    public void ComputerNotFoundAndDisabled_AreIndependentBuckets()
    {
        var item = MakeItem(1);
        item.ADComputerNotFound = "No matching AD computer object found";
        item.ADComputerDisabled = "Computer object disabled in AD";
        var summary = TabStatusSummaryCalculator.Compute([item]);
        Assert.Equal(1, summary.ComputerNotFound);
        Assert.Equal(1, summary.ComputerDisabled);
    }

    [Fact]
    public void MultipleRows_CountsAggregateCorrectly()
    {
        var a = MakeItem(1);
        a["SerialNumber"] = "";
        var b = MakeItem(2);
        b["SerialNumber"] = "SN-2";
        b.ADFlagReason = "AD account disabled";
        var summary = TabStatusSummaryCalculator.Compute([a, b]);
        Assert.Equal(2, summary.Total);
        Assert.Equal(1, summary.MissingSerial);
        Assert.Equal(1, summary.AdDisabled);
    }
}
