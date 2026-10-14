using InventoryManager.Core.Ad;

namespace InventoryManager.Core.Tests;

/// <summary>
/// In-memory stand-in for a domain, used to unit-test OnboardingService (and later Transfer/
/// Offboarding) without ever touching real AD. Deliberately minimal - only what those services
/// actually call.
/// </summary>
public sealed class FakeAdService : IAdService
{
    public AdCredentialContext? Credentials { get; set; }
    public Dictionary<string, AdUserInfo> Users { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, AdComputerInfo> Computers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> GroupSids { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> GroupMemberships { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string? DomainSid { get; set; } = "S-1-5-21-1-2-3";
    public string? DomainNetBiosName { get; set; } = "TESTDOM";
    public List<string> CreateUserCalls { get; } = [];
    /// <summary>Captures the last CreateUser call's provenance-marker attribute/value, so a test
    /// can actually assert the marker was passed through - the real AdService writes this to a
    /// real LDAP attribute (see OnboardingMarker), which a fake in-memory user has no equivalent
    /// field for.</summary>
    public (string Attribute, string Value)? LastProvenanceMarker { get; private set; }
    /// <summary>Captures the full last CreateUser request so a test can assert on fields
    /// (JobTitle/Department/Company/LogonScript) that AdUserInfo itself has no equivalent property
    /// for - same rationale as LastProvenanceMarker above.</summary>
    public NewAdUserRequest? LastCreateUserRequest { get; private set; }
    public List<(string Identity, string Group)> AddToGroupCalls { get; } = [];
    public List<(string Identity, string? Description)> ComputerDescriptionCalls { get; } = [];
    public Func<string, string, string, bool>? ValidateCredentialsOverride { get; set; }
    public Func<string, string, string, bool>? IsUserInGroupOverride { get; set; }

    // ---- Phase 5 (Offboarding) additions ----
    public Dictionary<string, List<string>> GroupDnsByUser { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> ResetPasswords { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> ExistingOrganizationalUnits { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool ExchangeSchemaSupported { get; set; } = true;
    public Dictionary<string, bool> HiddenFromAddressLists { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> GroupsThatFailToRemove { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>When set, AddUserToGroup throws for this exact group name - simulates a real AD
    /// failure (e.g. insufficient rights) independent of the privileged-group deny-list.</summary>
    public HashSet<string> GroupsThatFailToAdd { get; } = new(StringComparer.OrdinalIgnoreCase);

    // ---- AD Sync / Check AD additions ----
    public List<AdUserMatchInfo> AllUsers { get; } = [];
    public Dictionary<string, List<AdComputerMatchInfo>> ComputersByOu { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<AdUserChangeInfo> RecentlyChangedUsers { get; } = [];

    public IReadOnlyList<AdUserMatchInfo> ListAllUsers() => AllUsers;

    // ---- Protected-account additions ----
    public HashSet<string> PrivilegedUsers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool FailPrivilegedLookup { get; set; }

    public IReadOnlyList<string> ListPrivilegedUserSamAccountNames(string? domainSid) =>
        FailPrivilegedLookup ? throw new InvalidOperationException("AD unavailable") : PrivilegedUsers.ToList();

    public IReadOnlyList<AdComputerMatchInfo> ListComputersInOu(string organizationalUnitDn) =>
        ComputersByOu.TryGetValue(organizationalUnitDn, out var list) ? list : [];

    public IReadOnlyList<AdUserChangeInfo> GetRecentlyChangedUsers(int days) => RecentlyChangedUsers;

    public bool ValidateCredentials(string domain, string username, string password) =>
        ValidateCredentialsOverride?.Invoke(domain, username, password) ?? false;

    public bool IsUserInGroupTransitive(string domain, string username, string groupName) =>
        IsUserInGroupOverride?.Invoke(domain, username, groupName) ?? false;

    public string? GetDomainNetBiosName() => DomainNetBiosName;

    public string? GetDomainSid() => DomainSid;

    public AdUserInfo? GetUser(string identity) => Users.GetValueOrDefault(identity);

    public AdUserInfo? GetUserByDistinguishedName(string distinguishedName) =>
        Users.Values.FirstOrDefault(u => u.DistinguishedName == distinguishedName);

    public IReadOnlyList<AdUserSummary> SearchUsersByName(string term) =>
        Users.Values
            .Where(u => u.GivenName.Contains(term, StringComparison.OrdinalIgnoreCase) || u.Surname.Contains(term, StringComparison.OrdinalIgnoreCase))
            .Select(u => new AdUserSummary(u.SamAccountName, u.GivenName, u.Surname, "", u.DistinguishedName))
            .ToList();

    public IReadOnlyList<AdUserSummary> SearchUsersByExactSurname(string surname) =>
        Users.Values
            .Where(u => string.Equals(u.Surname, surname, StringComparison.OrdinalIgnoreCase))
            .Select(u => new AdUserSummary(u.SamAccountName, u.GivenName, u.Surname, "", u.DistinguishedName))
            .ToList();

    public AdUserInfo CreateUser(NewAdUserRequest request)
    {
        CreateUserCalls.Add(request.SamAccountName);
        LastCreateUserRequest = request;
        if (!string.IsNullOrWhiteSpace(request.ProvenanceMarkerAttribute))
        {
            LastProvenanceMarker = (request.ProvenanceMarkerAttribute, request.ProvenanceMarkerValue);
        }
        var dn = $"CN={request.GivenName} {request.Surname},{request.OrganizationalUnitDn}";
        var user = new AdUserInfo(
            request.SamAccountName,
            dn,
            request.GivenName,
            request.Surname,
            Enabled: true,
            request.ManagerDistinguishedName,
            request.Description,
            request.Office,
            request.InfoNote);
        Users[request.SamAccountName] = user;
        return user;
    }

    public void SetUserManager(string identity, string managerDistinguishedName) =>
        Users[identity] = Users[identity] with { ManagerDistinguishedName = managerDistinguishedName };

    public void SetUserDescription(string identity, string? description) =>
        Users[identity] = Users[identity] with { Description = description ?? "" };

    public void SetUserOffice(string identity, string? office) =>
        Users[identity] = Users[identity] with { Office = office ?? "" };

    public void SetUserInfo(string identity, string info) =>
        Users[identity] = Users[identity] with { Info = info };

    public void MoveUser(string distinguishedName, string targetOrganizationalUnitDn)
    {
        var entry = Users.Values.FirstOrDefault(u => u.DistinguishedName == distinguishedName);
        if (entry is not null)
        {
            Users[entry.SamAccountName] = entry with { DistinguishedName = $"{distinguishedName}-MOVED-TO-{targetOrganizationalUnitDn}" };
        }
    }

    public void AddUserToGroup(string identity, string groupName)
    {
        AddToGroupCalls.Add((identity, groupName));
        if (GroupsThatFailToAdd.Contains(groupName))
        {
            throw new InvalidOperationException($"Simulated failure adding to '{groupName}'.");
        }
        GroupMemberships.Add($"{identity}::{groupName}");
    }

    public AdComputerInfo? GetComputer(string identity) => Computers.GetValueOrDefault(identity);

    public void SetComputerEnabled(string identity, bool enabled) =>
        Computers[identity] = Computers[identity] with { Enabled = enabled };

    public void SetComputerDescription(string identity, string? description)
    {
        ComputerDescriptionCalls.Add((identity, description));
        if (!Computers.TryGetValue(identity, out var computer))
        {
            throw new InvalidOperationException($"Computer '{identity}' not found.");
        }
        Computers[identity] = computer with { Description = description ?? "" };
    }

    public List<(string Identity, string TargetOuDn)> MoveComputerCalls { get; } = [];

    public void MoveComputer(string identity, string targetOrganizationalUnitDn)
    {
        MoveComputerCalls.Add((identity, targetOrganizationalUnitDn));
        if (!Computers.TryGetValue(identity, out var computer))
        {
            throw new InvalidOperationException($"Computer '{identity}' not found.");
        }
        var cn = computer.DistinguishedName.Split(',')[0];
        Computers[identity] = computer with { DistinguishedName = $"{cn},{targetOrganizationalUnitDn}" };
    }

    public void SetUserEnabled(string identity, bool enabled) =>
        Users[identity] = Users[identity] with { Enabled = enabled };

    public void ResetUserPassword(string identity, string newPassword) =>
        ResetPasswords[identity] = newPassword;

    public bool SupportsExchangeSchema() => ExchangeSchemaSupported;

    public void SetUserHiddenFromAddressLists(string identity, bool hidden) =>
        HiddenFromAddressLists[identity] = hidden;

    public IReadOnlyList<string> GetUserGroupDistinguishedNames(string identity) =>
        GroupDnsByUser.TryGetValue(identity, out var groups) ? groups : [];

    public void RemoveUserFromGroup(string identity, string groupDistinguishedName)
    {
        if (GroupsThatFailToRemove.Contains(groupDistinguishedName))
        {
            throw new InvalidOperationException($"Simulated failure removing from '{groupDistinguishedName}'.");
        }
        GroupDnsByUser.GetValueOrDefault(identity)?.Remove(groupDistinguishedName);
    }

    public bool OrganizationalUnitExists(string organizationalUnitDn) => ExistingOrganizationalUnits.Contains(organizationalUnitDn);

    public string? GetGroupSid(string groupName) => GroupSids.GetValueOrDefault(groupName);

    /// <summary>Test hook: parent-group SIDs per group name, for the privilege-nesting check.
    /// Empty by default so existing tests see no nesting.</summary>
    public Dictionary<string, List<string>> GroupParentSids { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> GetGroupParentGroupSids(string groupName) =>
        GroupParentSids.TryGetValue(groupName, out var sids) ? sids : [];

    public IReadOnlyList<AdGroupSummary> SearchGroupsByName(string term) =>
        GroupSids.Keys.Where(g => g.Contains(term, StringComparison.OrdinalIgnoreCase))
            .Select(g => new AdGroupSummary(g, $"CN={g},OU=Groups,DC=test,DC=local", GroupSids[g]))
            .ToList();

    public IReadOnlyList<AdOrganizationalUnitInfo> ListOrganizationalUnits() => [];

    public bool SamAccountNameExists(string samAccountName) => Users.ContainsKey(samAccountName);

    // ---- Copy User Permissions additions ----
    public Dictionary<string, string> UserSids { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Groups resolvable by DN. A DN listed in a user's GroupDnsByUser but missing here
    /// simulates an unreadable group.</summary>
    public Dictionary<string, AdGroupInfo> GroupsByDn { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<(string Identity, string GroupDn)> AddToGroupByDnCalls { get; } = [];
    public HashSet<string> GroupDnsThatFailToAdd { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string? GetUserSid(string identity) => UserSids.GetValueOrDefault(identity);

    public AdGroupInfo? GetGroupByDistinguishedName(string groupDistinguishedName) =>
        GroupsByDn.GetValueOrDefault(groupDistinguishedName);

    public void AddUserToGroupByDistinguishedName(string identity, string groupDistinguishedName)
    {
        AddToGroupByDnCalls.Add((identity, groupDistinguishedName));
        if (GroupDnsThatFailToAdd.Contains(groupDistinguishedName))
        {
            throw new InvalidOperationException($"Simulated failure adding to '{groupDistinguishedName}'.");
        }
        if (!GroupDnsByUser.TryGetValue(identity, out var list))
        {
            GroupDnsByUser[identity] = list = [];
        }
        list.Add(groupDistinguishedName);
    }

    /// <summary>Test helper: registers a group resolvable by both DN and name.</summary>
    public string AddGroup(string samAccountName, string sid, bool security = true)
    {
        var dn = $"CN={samAccountName},OU=Groups,DC=test,DC=local";
        GroupsByDn[dn] = new AdGroupInfo(samAccountName, samAccountName, dn, sid, security, "Global");
        GroupSids[samAccountName] = sid;
        return dn;
    }
}
