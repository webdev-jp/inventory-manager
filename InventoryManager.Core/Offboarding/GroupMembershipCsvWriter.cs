using System.Text;

namespace InventoryManager.Core.Offboarding;

/// <summary>
/// Direct port of the PowerShell build's "ExportGroups" step (Export-Csv of MemberOf DNs). Uses
/// the shared <see cref="InventoryManager.Core.Csv"/> writer so this export gets the same CSV/DDE
/// formula-injection neutralization every other export does - a group DN or sAMAccountName that
/// begins with a formula-trigger character (e.g. a maliciously named AD object whose CN starts
/// with '=' or '-') can otherwise execute when the exported file is opened in Excel.
/// </summary>
public static class GroupMembershipCsvWriter
{
    public static string BuildCsv(string samAccountName, IReadOnlyList<string> groupDistinguishedNames)
    {
        var sb = new StringBuilder();
        sb.Append(Csv.WriteRow(["User", "GroupDN"])).Append("\r\n");
        foreach (var dn in groupDistinguishedNames)
        {
            sb.Append(Csv.WriteRow([samAccountName, dn])).Append("\r\n");
        }
        return sb.ToString();
    }

    public static void WriteCsvFile(string path, string samAccountName, IReadOnlyList<string> groupDistinguishedNames) =>
        File.WriteAllText(path, BuildCsv(samAccountName, groupDistinguishedNames));
}
