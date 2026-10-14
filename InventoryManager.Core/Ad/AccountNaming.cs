using System.Text.RegularExpressions;

namespace InventoryManager.Core.Ad;

/// <summary>
/// Direct port of the PowerShell build's Get-SanitizedAccountNameBase / Get-UniqueSamAccountName.
/// Pure functions - the existence check is injected as a delegate so this is fully unit-testable
/// without a real AD call.
/// </summary>
public static partial class AccountNaming
{
    /// <summary>Last name + first initial, lowercase - the org's confirmed SamAccountName
    /// convention (e.g. Smith, John -> "smithj"). SamAccountName has a restricted character set,
    /// so anything that isn't a-z/0-9 (apostrophes, hyphens, spaces in names like O'Brien or
    /// Smith-Jones) is stripped rather than passed through.</summary>
    public static string SanitizeBase(string lastName, string firstName)
    {
        var lastClean = NonAlphaNumeric().Replace(lastName ?? "", "").ToLowerInvariant();
        var firstInitial = !string.IsNullOrEmpty(firstName)
            ? NonAlphaNumeric().Replace(firstName[..1], "")
            : "";
        return lastClean + firstInitial.ToLowerInvariant();
    }

    /// <summary>Appends an incrementing digit on collision (base2, base3, ...), checked via the
    /// supplied existence check regardless of the existing account's Enabled state - a disabled
    /// account still occupies that name.</summary>
    public static string GenerateUnique(string baseName, Func<string, bool> exists)
    {
        if (!exists(baseName))
        {
            return baseName;
        }
        var suffix = 2;
        while (exists($"{baseName}{suffix}"))
        {
            suffix++;
        }
        return $"{baseName}{suffix}";
    }

    [GeneratedRegex("[^a-zA-Z0-9]")]
    private static partial Regex NonAlphaNumeric();
}
