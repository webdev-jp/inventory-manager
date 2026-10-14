namespace InventoryManager.Core.CellPhones;

/// <summary>Parses a carrier device-report CSV (Francois, 2026-09-23: "I have a list of cell
/// phones in a csv") into the 3 columns this feature cares about - "User name", "Wireless number",
/// and "Device model" - located by header name (case-insensitive, trimmed) rather than fixed
/// position, since a real carrier export carries many more columns than these three and their order
/// isn't guaranteed. "Device model" is optional (its column may be missing entirely and the field
/// is just blank per row); "User name"/"Wireless number" are required - a report without them isn't
/// usable for matching at all, so <see cref="Parse"/> throws rather than silently producing nothing
/// useful.</summary>
public static class CellPhoneCsvParser
{
    public static List<CellPhoneCsvRow> Parse(string content)
    {
        var rows = Csv.Parse(content);
        if (rows.Count == 0)
        {
            return [];
        }

        var header = rows[0];
        var nameCol = IndexOfHeader(header, "User name");
        var wirelessCol = IndexOfHeader(header, "Wireless number");
        var modelCol = IndexOfHeader(header, "Device model");

        if (nameCol < 0 || wirelessCol < 0)
        {
            throw new InvalidOperationException("This CSV is missing a 'User name' or 'Wireless number' column - it doesn't look like a device report this feature can read.");
        }

        var result = new List<CellPhoneCsvRow>();
        for (var i = 1; i < rows.Count; i++)
        {
            var row = rows[i];
            var name = GetCell(row, nameCol).Trim();
            var wireless = GetCell(row, wirelessCol).Trim();
            if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(wireless))
            {
                continue;
            }
            var model = modelCol >= 0 ? GetCell(row, modelCol).Trim() : "";
            result.Add(new CellPhoneCsvRow(name, wireless, model));
        }
        return result;
    }

    private static string GetCell(string[] row, int index) => index >= 0 && index < row.Length ? row[index] : "";

    private static int IndexOfHeader(string[] header, string name) =>
        Array.FindIndex(header, h => string.Equals(h.Trim(), name, StringComparison.OrdinalIgnoreCase));
}
