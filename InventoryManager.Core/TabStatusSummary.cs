using InventoryManager.Core.Models;

namespace InventoryManager.Core;

/// <summary>Direct port of the PowerShell build's Update-TabStatusLabel formula.</summary>
public sealed record TabStatusSummary(int Total, int MissingSerial, int AdDisabled, int UserNotFound, int ComputerNotFound, int ComputerDisabled)
{
    public override string ToString() =>
        $"{Total} total, {MissingSerial} no Serial #, {AdDisabled} AD-disabled, {UserNotFound} user not in AD, {ComputerNotFound} computer not in AD, {ComputerDisabled} computer disabled";
}

public static class TabStatusSummaryCalculator
{
    public static TabStatusSummary Compute(IEnumerable<InventoryItem> rows)
    {
        var rowList = rows.ToList();
        return new TabStatusSummary(
            Total: rowList.Count,
            MissingSerial: rowList.Count(i => string.IsNullOrWhiteSpace(i["SerialNumber"])),
            AdDisabled: rowList.Count(i => i.ADFlagReason == "AD account disabled"),
            UserNotFound: rowList.Count(i => i.ADFlagReason == "No matching AD user found"),
            ComputerNotFound: rowList.Count(i => !string.IsNullOrWhiteSpace(i.ADComputerNotFound)),
            ComputerDisabled: rowList.Count(i => !string.IsNullOrWhiteSpace(i.ADComputerDisabled)));
    }
}
