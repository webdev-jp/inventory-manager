namespace InventoryManager.Core.Ad;

public sealed record AdUserInfo(
    string SamAccountName,
    string DistinguishedName,
    string GivenName,
    string Surname,
    bool Enabled,
    string? ManagerDistinguishedName,
    string Description,
    string Office,
    string Info);

public sealed record AdUserSummary(string SamAccountName, string GivenName, string Surname, string Title, string DistinguishedName);

/// <summary>Sid is fetched in the same LDAP query as Name/DistinguishedName (SearchGroupsByName)
/// specifically so GroupPickerDialog can privilege-filter an entire domain's worth of groups
/// without an extra per-group AD round trip for each one.</summary>
public sealed record AdGroupSummary(string Name, string DistinguishedName, string? Sid = null);

/// <summary>One group resolved from a trusted DN (e.g. read back from a user's memberOf) - used by
/// Copy User Permissions, which needs the sAMAccountName for the privileged-group checks (they key
/// on name) plus the SID and group type in one read.</summary>
public sealed record AdGroupInfo(
    string SamAccountName,
    string Name,
    string DistinguishedName,
    string? Sid,
    bool IsSecurityGroup,
    string Scope);

public sealed record AdComputerInfo(string Name, string DistinguishedName, bool Enabled, string Description);

public sealed record AdOrganizationalUnitInfo(string Name, string DistinguishedName);

/// <summary>Bulk-fetch shape for AdUserMatchCheckService - just enough per user to classify every
/// inventory row in one pass without a per-row AD round trip.</summary>
public sealed record AdUserMatchInfo(string SamAccountName, string GivenName, string Surname, bool Enabled);

/// <summary>Bulk-fetch shape for AdUserMatchCheckService's computer axis.</summary>
public sealed record AdComputerMatchInfo(string Name, string DistinguishedName, bool Enabled);

/// <summary>A user changed within the Active Directory Sync dashboard's lookback window.</summary>
public sealed record AdUserChangeInfo(string GivenName, string Surname, bool Enabled, DateTime? WhenChanged, DateTime? WhenCreated);

/// <summary>Everything needed to create a new AD user in one call - mirrors the single
/// New-ADUser call the PowerShell build makes so an account is never briefly incomplete.</summary>
public sealed record NewAdUserRequest(
    string GivenName,
    string Surname,
    string SamAccountName,
    string UserPrincipalName,
    string Password,
    string OrganizationalUnitDn,
    string? ManagerDistinguishedName,
    string Description,
    string Office,
    string JobTitle,
    string Department,
    string Company,
    string LogonScript,
    string InfoNote,
    bool MustChangePasswordAtLogon,
    string ProvenanceMarkerAttribute,
    string ProvenanceMarkerValue);
