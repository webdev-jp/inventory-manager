using System.Text;

namespace InventoryManager.Core;

/// <summary>Minimal, dependency-free RFC 4180-ish CSV reader/writer - quoted fields (embedded
/// commas, quotes doubled, embedded CR/LF) both ways, nothing fancier.
///
/// Security: <see cref="WriteRow"/> also neutralizes spreadsheet formula/DDE injection
/// (CWE-1236). A cell whose first character is one of = + - @ TAB CR is a live formula when the
/// file is opened in Excel/LibreOffice - e.g. a Device Name of <c>=cmd|'/c calc'!A1</c> silently
/// launches a process in the operator who opens the export. RFC 4180 quoting does NOT stop this:
/// the spreadsheet strips the surrounding quotes first, then still sees the leading '='. Since
/// inventory data is multi-user and can be seeded from imported CSVs or a shared inventory.json,
/// every export is treated as untrusted and each risky cell is prefixed with a single apostrophe
/// (which Excel renders invisibly, as "treat as text"). <see cref="StripFormulaGuard"/> reverses
/// exactly that prefix on import so this app's own export -> import round-trip is lossless.</summary>
public static class Csv
{
    private const string FormulaTriggers = "=+-@\t\r";

    public static string WriteRow(IEnumerable<string> fields) =>
        string.Join(",", fields.Select(f => EscapeField(NeutralizeFormula(f))));

    /// <summary>If <paramref name="value"/> begins with a spreadsheet formula-trigger character
    /// (= + - @ TAB CR), prefix it with an apostrophe so Excel/LibreOffice treat it as literal
    /// text instead of evaluating it. Idempotent for already-safe values (returned unchanged).</summary>
    public static string NeutralizeFormula(string value)
    {
        if (!string.IsNullOrEmpty(value) && FormulaTriggers.IndexOf(value[0]) >= 0)
        {
            return "'" + value;
        }
        return value;
    }

    /// <summary>Reverses <see cref="NeutralizeFormula"/>: drops a single leading apostrophe when it
    /// is immediately followed by a formula-trigger character, so a value this app exported and
    /// then re-imports comes back byte-for-byte. A genuinely apostrophe-prefixed value in a
    /// hand-made external CSV loses one apostrophe - an accepted, rare cost for closing the
    /// injection vector.</summary>
    public static string StripFormulaGuard(string value)
    {
        if (value.Length >= 2 && value[0] == '\'' && FormulaTriggers.IndexOf(value[1]) >= 0)
        {
            return value[1..];
        }
        return value;
    }

    private static string EscapeField(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
        {
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
        return value;
    }

    /// <summary>Parses CSV text into rows of raw string fields - the first row is not treated
    /// specially here, callers decide whether it's a header.</summary>
    public static List<string[]> Parse(string content)
    {
        var rows = new List<string[]>();
        var currentRow = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var i = 0;
        var sawAnyField = false;

        void EndField()
        {
            currentRow.Add(field.ToString());
            field.Clear();
        }

        void EndRow()
        {
            EndField();
            rows.Add(currentRow.ToArray());
            currentRow = [];
            sawAnyField = false;
        }

        while (i < content.Length)
        {
            var c = content[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < content.Length && content[i + 1] == '"')
                    {
                        field.Append('"');
                        i += 2;
                        continue;
                    }
                    inQuotes = false;
                    i++;
                    continue;
                }
                field.Append(c);
                i++;
                continue;
            }

            switch (c)
            {
                case '"':
                    inQuotes = true;
                    sawAnyField = true;
                    i++;
                    break;
                case ',':
                    sawAnyField = true;
                    EndField();
                    i++;
                    break;
                case '\r':
                    if (i + 1 < content.Length && content[i + 1] == '\n')
                    {
                        i++;
                    }
                    EndRow();
                    i++;
                    break;
                case '\n':
                    EndRow();
                    i++;
                    break;
                default:
                    sawAnyField = true;
                    field.Append(c);
                    i++;
                    break;
            }
        }
        if (sawAnyField || field.Length > 0 || currentRow.Count > 0)
        {
            EndRow();
        }
        return rows;
    }
}
