using System.Net;
using System.Security;

namespace InventoryManager.Core.Ad;

/// <summary>
/// Port of the PowerShell build's $script:AdCredential / Get-AdCredParams pattern: an optional
/// alternate AD identity for this session only, never written to disk. When set, every AD
/// operation runs as this account instead of whoever is logged into Windows - this is how the SU
/// login's own credential ends up being the identity that actually performs AD writes.
/// </summary>
public sealed class AdCredentialContext
{
    public string? Domain { get; }
    public string Username { get; }
    private readonly SecureString _password;

    public AdCredentialContext(string username, SecureString password, string? domain = null)
    {
        Username = username;
        _password = password;
        Domain = domain;
    }

    public static AdCredentialContext FromPlainText(string username, string password, string? domain = null)
    {
        var secure = new SecureString();
        foreach (var c in password)
        {
            secure.AppendChar(c);
        }
        secure.MakeReadOnly();
        return new AdCredentialContext(username, secure, domain);
    }

    /// <summary>"DOMAIN\username" or "username@domain" as typed, unchanged - callers that need
    /// NetBIOS-qualification (e.g. before handing this to PrincipalContext) resolve that
    /// separately via IAdService.GetDomainNetBiosName, same as the PowerShell build does.</summary>
    public NetworkCredential ToNetworkCredential() => new(Username, _password, Domain);
}
