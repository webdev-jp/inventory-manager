using InventoryManager.Core.Models;

namespace InventoryManager.Core;

/// <summary>
/// Direct port of the PowerShell build's Import-ItemsCsv/Build-CsvHeaderMap/Get-CsvFieldValue: a
/// column matches a field by its Key, Label, or any of its Aliases (case-insensitive, trimmed) -
/// so both a fresh export from this app and an old hand-maintained spreadsheet with slightly
/// different header names ("Serial" instead of "SerialNumber") import cleanly. A row with no
/// Device Name but a Last or First Name still gets a synthetic "No PC" device (assigned-user-only
/// stock records exist in real inventories); a row with none of the three is skipped entirely.
/// </summary>
public static class InventoryCsvImporter
{
    public sealed record ImportResult(IReadOnlyList<InventoryItem> Added, int Skipped);

    public static ImportResult Import(string csvContent, string targetStatus, Func<int> nextId)
    {
        var rows = Csv.Parse(csvContent);
        if (rows.Count == 0)
        {
            return new ImportResult([], 0);
        }

        var headerMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var header = rows[0];
        for (var col = 0; col < header.Length; col++)
        {
            var key = header[col].Trim();
            if (!headerMap.ContainsKey(key))
            {
                headerMap[key] = col;
            }
        }

        var deviceNameField = InventorySchema.FieldDefs.First(f => f.Key == "DeviceName");
        var lastNameField = InventorySchema.FieldDefs.First(f => f.Key == "LastName");
        var firstNameField = InventorySchema.FieldDefs.First(f => f.Key == "FirstName");

        string GetValue(string[] row, FieldDefinition field)
        {
            var candidates = new List<string>(field.Aliases.Length + 2) { field.Key, field.Label };
            candidates.AddRange(field.Aliases);
            foreach (var candidate in candidates)
            {
                if (headerMap.TryGetValue(candidate.Trim(), out var col) && col < row.Length)
                {
                    // Reverse the export-time formula-injection guard (see Csv.NeutralizeFormula)
                    // so a value this app exported round-trips byte-for-byte on re-import.
                    return Csv.StripFormulaGuard(row[col]);
                }
            }
            return "";
        }

        var added = new List<InventoryItem>();
        var skipped = 0;
        for (var r = 1; r < rows.Count; r++)
        {
            var row = rows[r];
            if (row.Length == 1 && row[0].Length == 0)
            {
                continue;
            }

            var deviceNameVal = GetValue(row, deviceNameField).Trim();
            if (string.IsNullOrWhiteSpace(deviceNameVal))
            {
                var lastNameVal = GetValue(row, lastNameField).Trim();
                var firstNameVal = GetValue(row, firstNameField).Trim();
                if (string.IsNullOrWhiteSpace(lastNameVal) && string.IsNullOrWhiteSpace(firstNameVal))
                {
                    skipped++;
                    continue;
                }
                deviceNameVal = InventorySchema.NoDevicePlaceholder;
            }

            var item = new InventoryItem { Id = nextId(), Status = targetStatus };
            foreach (var field in InventorySchema.FieldDefs)
            {
                if (field.Key == "DeviceName")
                {
                    item[field.Key] = deviceNameVal;
                    continue;
                }
                var value = GetValue(row, field);
                item[field.Key] = field.Type == FieldType.Dropdown ? SanitizeDropdownValue(field, value) : value.Trim();
            }
            added.Add(item);
        }

        return new ImportResult(added, skipped);
    }

    private static string SanitizeDropdownValue(FieldDefinition field, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return field.Default;
        }
        foreach (var option in field.Options)
        {
            if (option == value)
            {
                return option;
            }
        }
        return field.Default;
    }
}
