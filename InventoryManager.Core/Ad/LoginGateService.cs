namespace InventoryManager.Core.Ad;

public enum SuLoginAttemptOutcome
{
    Success,
    EmptyUsername,
    InvalidCredentials,
    NotInSuGroup,
}

public sealed record SuLoginAttemptResult(SuLoginAttemptOutcome Outcome, string? QualifiedUsername = null);

/// <summary>
/// Pure decision logic behind the PowerShell build's two-layer login gate (Invoke-AccessGate /
/// Invoke-SuLogin), separated from the WinForms dialog loop so it's unit-testable against
/// FakeAdService - the same discipline the rest of this project applies to AD-touching logic.
/// Each failed attempt is a real AD credential validation, so the domain's own account lockout
/// policy applies exactly as it would to any other logon; the caller's own attempt cap is a
/// courtesy against fat-fingering, not the real brute-force defense.
/// </summary>
public static class LoginGateService
{
    /// <summary>The gate account check: "is this operator allowed to even launch the tool at
    /// all." A passphrase check only - no group membership, no lasting credential.</summary>
    public static bool CheckGateAccount(IAdService adService, string domain, string username, string password) =>
        adService.ValidateCredentials(domain, username, password);

    /// <summary>The operator's own AD login, gated on SU-group membership (checked transitively,
    /// so nested membership counts). On success, returns the domain-qualified username the
    /// resulting credential should authenticate as.</summary>
    public static SuLoginAttemptResult AttemptSuLogin(IAdService adService, string domain, string suGroupName, string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return new SuLoginAttemptResult(SuLoginAttemptOutcome.EmptyUsername);
        }
        if (!adService.ValidateCredentials(domain, username, password))
        {
            return new SuLoginAttemptResult(SuLoginAttemptOutcome.InvalidCredentials);
        }
        if (!adService.IsUserInGroupTransitive(domain, username, suGroupName))
        {
            return new SuLoginAttemptResult(SuLoginAttemptOutcome.NotInSuGroup);
        }
        return new SuLoginAttemptResult(SuLoginAttemptOutcome.Success, QualifyUsername(username, adService.GetDomainNetBiosName()));
    }

    /// <summary>"user" -&gt; "NETBIOS\user" so the resulting credential authenticates reliably
    /// wherever it's later used to bind - unchanged if already domain-qualified (contains '\' or
    /// '@') or if the NetBIOS name couldn't be resolved.</summary>
    public static string QualifyUsername(string username, string? netBiosDomain)
    {
        if (username.Contains('\\') || username.Contains('@') || netBiosDomain is null)
        {
            return username;
        }
        return $"{netBiosDomain}\\{username}";
    }
}
