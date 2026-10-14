namespace InventoryManager.Core.Ad;

/// <summary>
/// Direct port of the PowerShell build's Get-AdActorDisplayName: best-effort "First Last
/// (username)" for whoever is logged in as SU right now - falls back to the bare username if the
/// lookup fails, since this is only ever used for a human-readable audit trail (AD Notes
/// entries), never a security decision.
/// </summary>
public static class AdActorNameResolver
{
    public static string GetDisplayName(IAdService adService, string suUsername)
    {
        try
        {
            var user = adService.GetUser(suUsername);
            if (user is not null)
            {
                return $"{user.GivenName} {user.Surname} ({suUsername})".Trim();
            }
        }
        catch
        {
            // Fall back to just the username if the lookup fails - never block the caller over this.
        }
        return suUsername;
    }
}
