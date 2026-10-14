namespace InventoryManager.Core.Ad;

/// <summary>
/// Direct port of the PowerShell build's $script:AllowedUserOuBaseDn / Test-OuIsAllowedForNewUsers.
/// New users (Onboarding) and OU moves (Transfer User) may only target a sub-OU of this base -
/// never the base OU itself.
/// </summary>
public static class OuPolicy
{
    public const string AllowedUserOuBaseDn = "OU=Domain Users,DC=contoso,DC=local";

    /// <summary>
    /// Must be strictly WITHIN <see cref="AllowedUserOuBaseDn"/> - a sub-OU at any depth, never
    /// the base OU itself. String comparison is safe here because DNs from AD are always
    /// returned in the same canonical comma-separated form.
    /// </summary>
    public static bool IsAllowedForNewUsers(string? distinguishedName)
    {
        if (string.IsNullOrWhiteSpace(distinguishedName))
        {
            return false;
        }
        var dnLower = distinguishedName.Trim().ToLowerInvariant();
        var baseLower = AllowedUserOuBaseDn.ToLowerInvariant();
        return dnLower != baseLower && dnLower.EndsWith("," + baseLower);
    }
}
