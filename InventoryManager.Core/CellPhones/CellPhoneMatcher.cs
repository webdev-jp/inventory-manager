using InventoryManager.Core.Models;

namespace InventoryManager.Core.CellPhones;

/// <summary>One CSV row matched to exactly one inventory record.</summary>
public sealed record CellPhoneMatch(CellPhoneCsvRow CsvRow, InventoryItem Item);

/// <summary>One CSV row whose name matched more than one inventory record - needs a human to pick
/// which one, same idea as AdUserPickerDialog elsewhere in this app.</summary>
public sealed record CellPhoneAmbiguity(CellPhoneCsvRow CsvRow, IReadOnlyList<InventoryItem> Candidates);

public sealed record CellPhoneMatchResult(
    IReadOnlyList<CellPhoneMatch> Matched,
    IReadOnlyList<CellPhoneAmbiguity> Ambiguous,
    IReadOnlyList<CellPhoneCsvRow> Unmatched);

/// <summary>
/// Matches carrier device-report rows to existing Current Inventory records by person name, and
/// applies the accepted changes. Francois, 2026-09-23: "match what cell phone belongs to what user
/// ... match the users name (both first and last name) with the user name on the csv sheet."
/// <para>The CSV's "User name" column is one free-text field ("ROSA DIAZ"), but real names
/// aren't reliably First+Last: multi-word given names, two-surname Hispanic naming conventions, a
/// middle name, or even non-person shared-line names ("FRONT DESK PHONE") all show up in a real
/// carrier export. Rather than guessing a naming convention, <see cref="Match"/> tries EVERY
/// possible split point of the name's whitespace-separated tokens as a (First, Last) candidate pair
/// against the real FirstName/LastName values already in inventory - the inventory data itself is
/// the ground truth for which split is correct, so this works regardless of how many words the name
/// has. A name that matches no inventory record at any split point (a shared/department line, a
/// person not in Current Inventory) is Unmatched; a name that matches more than one distinct record
/// (either two employees sharing a name, or two different valid splits each hitting a different
/// real person) is Ambiguous.</para>
/// </summary>
public static class CellPhoneMatcher
{
    public static CellPhoneMatchResult Match(IReadOnlyList<CellPhoneCsvRow> csvRows, IReadOnlyList<InventoryItem> currentItems)
    {
        var matched = new List<CellPhoneMatch>();
        var ambiguous = new List<CellPhoneAmbiguity>();
        var unmatched = new List<CellPhoneCsvRow>();

        foreach (var row in csvRows)
        {
            var candidates = FindCandidates(row.RawUserName, currentItems);
            if (candidates.Count == 1)
            {
                matched.Add(new CellPhoneMatch(row, candidates[0]));
            }
            else if (candidates.Count > 1)
            {
                ambiguous.Add(new CellPhoneAmbiguity(row, candidates));
            }
            else
            {
                unmatched.Add(row);
            }
        }
        return new CellPhoneMatchResult(matched, ambiguous, unmatched);
    }

    /// <summary>Writes the CSV's wireless number into CellPhone, and its device model into
    /// DeviceModel - but only when the CSV actually has a model value, so a report with no model
    /// column never blanks out whatever was already recorded.</summary>
    public static void Apply(InventoryItem item, CellPhoneCsvRow row)
    {
        item["CellPhone"] = row.WirelessNumber;
        if (!string.IsNullOrWhiteSpace(row.DeviceModel))
        {
            item["DeviceModel"] = row.DeviceModel;
        }
    }

    private static List<InventoryItem> FindCandidates(string rawName, IReadOnlyList<InventoryItem> items)
    {
        var tokens = rawName.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length < 2)
        {
            return [];
        }

        var matchedIds = new HashSet<int>();
        var result = new List<InventoryItem>();
        for (var splitAt = 1; splitAt < tokens.Length; splitAt++)
        {
            var first = string.Join(' ', tokens[..splitAt]);
            var last = string.Join(' ', tokens[splitAt..]);
            foreach (var item in items)
            {
                if (matchedIds.Contains(item.Id))
                {
                    continue;
                }
                if (string.Equals(item.FirstName.Trim(), first, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(item.LastName.Trim(), last, StringComparison.OrdinalIgnoreCase))
                {
                    matchedIds.Add(item.Id);
                    result.Add(item);
                }
            }
        }
        return result;
    }
}
