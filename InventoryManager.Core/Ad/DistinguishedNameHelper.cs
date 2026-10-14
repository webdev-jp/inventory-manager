using System.Text.RegularExpressions;

namespace InventoryManager.Core.Ad;

public static partial class DistinguishedNameHelper
{
    /// <summary>
    /// Everything after the CN component of a DN - i.e. the parent OU's DN. AD escapes a literal
    /// comma inside the CN itself as "\," (a "Last, First" display name produces exactly that),
    /// so a naive split on the first comma lands in the middle of the name instead of at the real
    /// OU boundary. The pattern skips over any backslash-escaped character (comma included)
    /// before matching the real, unescaped separator.
    /// </summary>
    public static string GetParentOuDn(string distinguishedName)
    {
        var match = CnPrefixRegex().Match(distinguishedName ?? "");
        return match.Success ? match.Groups[1].Value : distinguishedName ?? "";
    }

    /// <summary>The unescaped value of a DN's first RDN ("CN=Smith\, Jo,OU=x" -> "Smith, Jo"), for
    /// display only. Empty if the DN doesn't parse.</summary>
    public static string GetFirstRdnValue(string distinguishedName)
    {
        var match = FirstRdnRegex().Match(distinguishedName ?? "");
        return match.Success ? Regex.Replace(match.Groups[1].Value, @"\\(.)", "$1") : "";
    }

    [GeneratedRegex(@"^CN=(?:\\.|[^,])*,(.+)$")]
    private static partial Regex CnPrefixRegex();

    [GeneratedRegex(@"^[A-Za-z]+=((?:\\.|[^,])*)")]
    private static partial Regex FirstRdnRegex();
}
