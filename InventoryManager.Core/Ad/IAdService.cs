namespace InventoryManager.Core.Ad;

/// <summary>
/// Everything this app needs from Active Directory, as one seam - lets the login gate,
/// privileged-group/OU policy checks, and (in later phases) Onboarding/Transfer/Offboarding be
/// unit-tested against a fake implementation instead of a real domain, the same way the
/// PowerShell build's own AD-touching functions were validated via isolated logic tests before
/// ever risking a real write.
/// </summary>
public interface IAdService
{
    /// <summary>The AD identity this instance's writes actually run as - the SU's own login
    /// credential, an operator-supplied "Run AD Actions As..." override, or null when neither is
    /// set and every call instead runs as whatever Windows session launched the app. Exposed so a
    /// caller that hands off to an external process (e.g. MainForm launching the offboarding
    /// script) can forward the SAME identity, instead of that process silently defaulting to the
    /// current Windows session and failing on AD delegation it doesn't have.</summary>
    AdCredentialContext? Credentials { get; }

    // ---- Login gate (System.DirectoryServices.AccountManagement's sweet spot) ----
    bool ValidateCredentials(string domain, string username, string password);
    bool IsUserInGroupTransitive(string domain, string username, string groupName);
    string? GetDomainNetBiosName();
    string? GetDomainSid();

    // ---- Users ----
    AdUserInfo? GetUser(string identity);
    /// <summary>Resolves a DN the caller already trusts (e.g. one just read back from a real
    /// GetUser() call), unlike GetUser(identity) which never treats its argument as a direct bind
    /// target - see the security note on AdService.FindEntry for why that distinction matters.</summary>
    AdUserInfo? GetUserByDistinguishedName(string distinguishedName);
    IReadOnlyList<AdUserSummary> SearchUsersByName(string term);
    /// <summary>Exact (not wildcard) Surname match - used by Check AD / Match User's name-based
    /// resolution, which needs "does exactly one AD user have this Last Name" rather than a
    /// broad substring search.</summary>
    IReadOnlyList<AdUserSummary> SearchUsersByExactSurname(string surname);
    AdUserInfo CreateUser(NewAdUserRequest request);
    void SetUserManager(string identity, string managerDistinguishedName);
    void SetUserDescription(string identity, string? description);
    void SetUserOffice(string identity, string? office);
    void MoveUser(string distinguishedName, string targetOrganizationalUnitDn);
    void SetUserInfo(string identity, string info);
    void AddUserToGroup(string identity, string groupName);

    // ---- Users - Offboarding-specific (Phase 5) ----
    void SetUserEnabled(string identity, bool enabled);
    void ResetUserPassword(string identity, string newPassword);

    /// <summary>Whether this forest's schema has the Exchange attribute extension applied at
    /// all - mirrors the offboarding script's own Initialize-AD detection
    /// (ldapDisplayName=msExchHideFromAddressLists in the schema naming context). Callers should
    /// skip the GAL-hide step entirely (not attempt-and-fail) when this is false, same as the
    /// PowerShell build.</summary>
    bool SupportsExchangeSchema();
    void SetUserHiddenFromAddressLists(string identity, bool hidden);
    IReadOnlyList<string> GetUserGroupDistinguishedNames(string identity);
    void RemoveUserFromGroup(string identity, string groupDistinguishedName);
    bool OrganizationalUnitExists(string organizationalUnitDn);

    // ---- Computers ----
    AdComputerInfo? GetComputer(string identity);
    void SetComputerEnabled(string identity, bool enabled);
    void SetComputerDescription(string identity, string? description);
    /// <summary>Moves the computer object to a different OU - e.g. out of Disabled Computers and
    /// into the Laptop OU when a device is returned to service. Unlike MoveUser (which takes a
    /// caller-supplied DN, since Transfer already has one from a prior GetUser call), this
    /// resolves the computer by identity itself, since callers here only ever have a device name
    /// on hand.</summary>
    void MoveComputer(string identity, string targetOrganizationalUnitDn);

    // ---- Groups / OUs ----
    string? GetGroupSid(string groupName);
    /// <summary>SIDs of every group the named group is transitively a member of (its parent
    /// groups up the nesting chain), used to catch a privileged group reached via nesting. See
    /// AdService.GetGroupParentGroupSids. Empty list if none or unresolved.</summary>
    IReadOnlyList<string> GetGroupParentGroupSids(string groupName);
    IReadOnlyList<AdGroupSummary> SearchGroupsByName(string term);
    /// <summary>sAMAccountNames of every user that is a member - directly, through nested groups,
    /// or as its primary group - of any group <see cref="PrivilegedGroupPolicy"/> treats as
    /// privileged. Feeds <see cref="ProtectedAccountPolicy"/>. Throws if AD can't be queried, so the
    /// caller can fail closed.</summary>
    IReadOnlyList<string> ListPrivilegedUserSamAccountNames(string? domainSid);
    IReadOnlyList<AdOrganizationalUnitInfo> ListOrganizationalUnits();

    // ---- Bulk fetch for AdUserMatchCheckService (Active Directory Sync / Check AD) ----
    IReadOnlyList<AdUserMatchInfo> ListAllUsers();
    IReadOnlyList<AdComputerMatchInfo> ListComputersInOu(string organizationalUnitDn);
    IReadOnlyList<AdUserChangeInfo> GetRecentlyChangedUsers(int days);

    // ---- Account name uniqueness (Onboarding's SamAccountName collision check) ----
    bool SamAccountNameExists(string samAccountName);

    // ---- Copy User Permissions ----
    /// <summary>The user's objectSid in string form (S-1-5-21-...), or null if not found. Needed
    /// to match NTFS ACEs, which reference SIDs, not names.</summary>
    string? GetUserSid(string identity);
    /// <summary>Resolves a group DN the caller already trusts (read back from a real memberOf),
    /// same trusted-DN rule as GetUserByDistinguishedName. Null if it can't be read.</summary>
    AdGroupInfo? GetGroupByDistinguishedName(string groupDistinguishedName);
    /// <summary>Adds the user to a group identified by a trusted DN - the add-side counterpart of
    /// RemoveUserFromGroup, since memberOf hands back DNs while AddUserToGroup takes a name.</summary>
    void AddUserToGroupByDistinguishedName(string identity, string groupDistinguishedName);
}
