using System.DirectoryServices;
using System.DirectoryServices.AccountManagement;
using System.Security.Principal;

namespace InventoryManager.Core.Ad;

/// <summary>
/// Real Active Directory implementation of <see cref="IAdService"/>. Uses
/// System.DirectoryServices.AccountManagement only for the login gate (its sweet spot -
/// ValidateCredentials/IsMemberOf) and raw System.DirectoryServices (DirectoryEntry/
/// DirectorySearcher) for everything else, since AccountManagement's UserPrincipal does not
/// expose manager/office/info or let a move-between-OUs happen - the PowerShell build's
/// Get/Set-ADUser etc. reach the same underlying LDAP attributes, just through a different
/// wrapper.
/// </summary>
public sealed class AdService : IAdService
{
    private readonly string _domainFqdn;
    private readonly AdCredentialContext? _credentials;

    public AdCredentialContext? Credentials => _credentials;

    public AdService(string domainFqdn, AdCredentialContext? credentials = null)
    {
        _domainFqdn = domainFqdn;
        _credentials = credentials;
    }

    private DirectoryEntry OpenRoot() => Open($"LDAP://{_domainFqdn}");

    private DirectoryEntry Open(string ldapPath)
    {
        if (_credentials is null)
        {
            return new DirectoryEntry(ldapPath);
        }
        var netCred = _credentials.ToNetworkCredential();
        return new DirectoryEntry(ldapPath, _credentials.Username, netCred.Password, AuthenticationTypes.Secure);
    }

    // ---- Login gate ----

    /// <summary>
    /// Deliberately does NOT use PrincipalContext.ValidateCredentials (ContextOptions.Negotiate) -
    /// Francois found LIVE, 2026-09-16, that it can accept BOTH an account's old password AND its
    /// brand-new one at the same time on this environment. That is the exact documented failure
    /// mode of Negotiate-based validation: under some AD/NTLM-fallback conditions it stops
    /// checking the supplied password at all and just confirms the username resolves to a real,
    /// enabled account - meaning (before this fix) the gate check, and the SU login that gates
    /// every privileged action in this app, could both be satisfied with ANY password for a valid
    /// username. Ruled out simpler explanations first (klist showed no cached Kerberos ticket for
    /// the account; the client binds directly to the PDC emulator, which rules out ordinary
    /// replication lag) before concluding this was the API itself.
    ///
    /// Fixed by forcing a real LDAP bind via DirectoryEntry instead: constructing a DirectoryEntry
    /// validates nothing by itself (ADSI binds lazily), so accessing NativeObject is what actually
    /// performs the bind - it throws on bad credentials and has no Negotiate-style fallback path
    /// that can silently skip the password check.
    /// </summary>
    public bool ValidateCredentials(string domain, string username, string password)
    {
        // Defense-in-depth against the LDAP unauthenticated-bind class (RFC 4513): a bind with a
        // real username but an EMPTY password can, on some directory configurations, succeed as an
        // anonymous/unauthenticated bind - which would make ValidateCredentials("suadmin", "")
        // return true. Reject a blank password (and blank username) before ever attempting the
        // bind. A real account never has an empty password, so this rejects nothing legitimate.
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            return false;
        }
        var qualifiedUsername = username.Contains('\\') || username.Contains('@')
            ? username
            : $"{username}@{domain}";
        try
        {
            using var entry = new DirectoryEntry($"LDAP://{domain}", qualifiedUsername, password, AuthenticationTypes.Secure);
            _ = entry.NativeObject;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool IsUserInGroupTransitive(string domain, string username, string groupName)
    {
        try
        {
            using var ctx = new PrincipalContext(ContextType.Domain, domain);
            using var user = UserPrincipal.FindByIdentity(ctx, username);
            if (user is null)
            {
                return false;
            }
            using var group = GroupPrincipal.FindByIdentity(ctx, groupName);
            if (group is null)
            {
                return false;
            }
            return user.IsMemberOf(group);
        }
        catch
        {
            return false;
        }
    }

    public string? GetDomainNetBiosName()
    {
        try
        {
            using var rootDse = new DirectoryEntry("LDAP://RootDSE");
            var configNc = rootDse.Properties["configurationNamingContext"].Value?.ToString();
            var defaultNc = rootDse.Properties["defaultNamingContext"].Value?.ToString();
            if (configNc is null || defaultNc is null)
            {
                return null;
            }
            using var partitions = new DirectoryEntry($"LDAP://CN=Partitions,{configNc}");
            using var searcher = new DirectorySearcher(partitions) { Filter = $"(ncName={defaultNc})" };
            searcher.PropertiesToLoad.Add("nETBIOSName");
            var result = searcher.FindOne();
            return result?.Properties["nETBIOSName"].Count > 0 ? result.Properties["nETBIOSName"][0]?.ToString() : null;
        }
        catch
        {
            return null;
        }
    }

    public string? GetDomainSid()
    {
        try
        {
            using var root = OpenRoot();
            if (root.Properties["objectSid"].Value is not byte[] sidBytes)
            {
                return null;
            }
            return new SecurityIdentifier(sidBytes, 0).Value;
        }
        catch
        {
            return null;
        }
    }

    // ---- Users ----

    public AdUserInfo? GetUser(string identity)
    {
        using var entry = FindEntry(identity, "user");
        return entry is null ? null : MapUser(entry);
    }

    /// <summary>Resolves a DN the caller already knows is genuine and trusted (e.g. a Manager DN
    /// just read back from a real GetUser() call moments earlier) - unlike GetUser(identity),
    /// which never treats its argument as a bind target (see the security note on FindEntry),
    /// this method always does, so it must never be called with a value that merely LOOKS like a
    /// DN but originated from unvalidated data (a CSV import, a hand-edited inventory.json).</summary>
    public AdUserInfo? GetUserByDistinguishedName(string distinguishedName)
    {
        try
        {
            using var entry = Open($"LDAP://{distinguishedName}");
            _ = entry.NativeGuid;
            return MapUser(entry);
        }
        catch
        {
            return null;
        }
    }

    public IReadOnlyList<AdUserSummary> SearchUsersByName(string term)
    {
        var safe = EscapeLdapFilterValue(term);
        using var root = OpenRoot();
        using var searcher = new DirectorySearcher(root)
        {
            Filter = $"(&(objectClass=user)(objectCategory=person)(|(name=*{safe}*)(givenName=*{safe}*)(sn=*{safe}*)))",
            PageSize = 1000,
        };
        foreach (var prop in new[] { "givenName", "sn", "sAMAccountName", "title", "distinguishedName" })
        {
            searcher.PropertiesToLoad.Add(prop);
        }
        var results = new List<AdUserSummary>();
        using var found = searcher.FindAll();
        foreach (SearchResult result in found)
        {
            results.Add(new AdUserSummary(
                GetProp(result, "sAMAccountName"),
                GetProp(result, "givenName"),
                GetProp(result, "sn"),
                GetProp(result, "title"),
                GetProp(result, "distinguishedName")));
        }
        return results;
    }

    public IReadOnlyList<AdUserSummary> SearchUsersByExactSurname(string surname)
    {
        var safe = EscapeLdapFilterValue(surname);
        using var root = OpenRoot();
        using var searcher = new DirectorySearcher(root)
        {
            Filter = $"(&(objectClass=user)(objectCategory=person)(sn={safe}))",
            PageSize = 1000,
        };
        foreach (var prop in new[] { "givenName", "sn", "sAMAccountName", "title", "distinguishedName" })
        {
            searcher.PropertiesToLoad.Add(prop);
        }
        var results = new List<AdUserSummary>();
        using var found = searcher.FindAll();
        foreach (SearchResult result in found)
        {
            results.Add(new AdUserSummary(
                GetProp(result, "sAMAccountName"),
                GetProp(result, "givenName"),
                GetProp(result, "sn"),
                GetProp(result, "title"),
                GetProp(result, "distinguishedName")));
        }
        return results;
    }

    public AdUserInfo CreateUser(NewAdUserRequest request)
    {
        using var ouEntry = Open($"LDAP://{request.OrganizationalUnitDn}");
        var displayName = $"{request.GivenName} {request.Surname}".Trim();
        using var newUser = (DirectoryEntry)ouEntry.Children.Add($"CN={EscapeDnComponent(displayName)}", "user");

        newUser.Properties["sAMAccountName"].Value = request.SamAccountName;
        newUser.Properties["userPrincipalName"].Value = request.UserPrincipalName;
        newUser.Properties["givenName"].Value = request.GivenName;
        newUser.Properties["sn"].Value = request.Surname;
        newUser.Properties["displayName"].Value = displayName;
        newUser.Properties["mail"].Value = request.UserPrincipalName;
        newUser.Properties["proxyAddresses"].Add($"SMTP:{request.UserPrincipalName}");
        if (!string.IsNullOrWhiteSpace(request.ManagerDistinguishedName))
        {
            newUser.Properties["manager"].Value = request.ManagerDistinguishedName;
        }
        if (!string.IsNullOrWhiteSpace(request.Description))
        {
            newUser.Properties["description"].Value = request.Description;
        }
        if (!string.IsNullOrWhiteSpace(request.Office))
        {
            newUser.Properties["physicalDeliveryOfficeName"].Value = request.Office;
        }
        if (!string.IsNullOrWhiteSpace(request.JobTitle))
        {
            newUser.Properties["title"].Value = request.JobTitle;
        }
        if (!string.IsNullOrWhiteSpace(request.Department))
        {
            newUser.Properties["department"].Value = request.Department;
        }
        if (!string.IsNullOrWhiteSpace(request.Company))
        {
            newUser.Properties["company"].Value = request.Company;
        }
        if (!string.IsNullOrWhiteSpace(request.LogonScript))
        {
            newUser.Properties["scriptPath"].Value = request.LogonScript;
        }
        if (!string.IsNullOrWhiteSpace(request.InfoNote))
        {
            newUser.Properties["info"].Value = request.InfoNote;
        }
        if (!string.IsNullOrWhiteSpace(request.ProvenanceMarkerAttribute))
        {
            newUser.Properties[request.ProvenanceMarkerAttribute].Value = request.ProvenanceMarkerValue;
        }
        newUser.CommitChanges();

        newUser.Invoke("SetPassword", request.Password);
        var uac = newUser.Properties["userAccountControl"].Value is int existingUac ? existingUac : 0x200;
        uac &= ~0x2; // ADS_UF_ACCOUNTDISABLE - clear it, enabling the account.
        // ADS_UF_PASSWD_NOTREQD - AD sets it on any user created without a password (546 = disabled +
        // not-required), and it lets the account have a blank password regardless of policy. Found
        // 2026-10-01 by the AD assessment tool on every account this app had onboarded.
        uac &= ~0x20;
        newUser.Properties["userAccountControl"].Value = uac;
        if (request.MustChangePasswordAtLogon)
        {
            newUser.Properties["pwdLastSet"].Value = 0;
        }
        newUser.CommitChanges();

        return GetUser(request.SamAccountName)
            ?? throw new InvalidOperationException($"User '{request.SamAccountName}' was created but could not be re-read.");
    }

    public void SetUserManager(string identity, string managerDistinguishedName)
    {
        using var entry = RequireEntry(identity, "user");
        entry.Properties["manager"].Value = managerDistinguishedName;
        entry.CommitChanges();
    }

    public void SetUserDescription(string identity, string? description) =>
        SetOrClearAttribute(identity, "user", "description", description);

    public void SetUserOffice(string identity, string? office) =>
        SetOrClearAttribute(identity, "user", "physicalDeliveryOfficeName", office);

    public void SetUserInfo(string identity, string info) =>
        SetOrClearAttribute(identity, "user", "info", info);

    public void MoveUser(string distinguishedName, string targetOrganizationalUnitDn)
    {
        using var entry = Open($"LDAP://{distinguishedName}");
        using var newParent = Open($"LDAP://{targetOrganizationalUnitDn}");
        entry.MoveTo(newParent);
    }

    public void AddUserToGroup(string identity, string groupName)
    {
        using var user = RequireEntry(identity, "user");
        using var group = RequireEntry(groupName, "group");
        group.Properties["member"].Add(user.Properties["distinguishedName"].Value);
        group.CommitChanges();
    }

    public void SetUserEnabled(string identity, bool enabled)
    {
        using var entry = RequireEntry(identity, "user");
        var uac = entry.Properties["userAccountControl"].Value is int existingUac ? existingUac : 0x200;
        uac = enabled ? uac & ~0x2 : uac | 0x2;
        entry.Properties["userAccountControl"].Value = uac;
        entry.CommitChanges();
    }

    public void ResetUserPassword(string identity, string newPassword)
    {
        using var entry = RequireEntry(identity, "user");
        entry.Invoke("SetPassword", newPassword);
    }

    public bool SupportsExchangeSchema()
    {
        try
        {
            using var rootDse = new DirectoryEntry("LDAP://RootDSE");
            var schemaNc = rootDse.Properties["schemaNamingContext"].Value?.ToString();
            if (schemaNc is null)
            {
                return false;
            }
            using var schemaRoot = Open($"LDAP://{schemaNc}");
            using var searcher = new DirectorySearcher(schemaRoot)
            {
                Filter = "(ldapDisplayName=msExchHideFromAddressLists)",
            };
            return searcher.FindOne() is not null;
        }
        catch
        {
            return false;
        }
    }

    public void SetUserHiddenFromAddressLists(string identity, bool hidden)
    {
        using var entry = RequireEntry(identity, "user");
        entry.Properties["msExchHideFromAddressLists"].Value = hidden;
        entry.CommitChanges();
    }

    public IReadOnlyList<string> GetUserGroupDistinguishedNames(string identity)
    {
        using var entry = RequireEntry(identity, "user");
        var values = entry.Properties["memberOf"];
        var result = new List<string>();
        foreach (var value in values)
        {
            if (value?.ToString() is { } dn)
            {
                result.Add(dn);
            }
        }
        return result;
    }

    public void RemoveUserFromGroup(string identity, string groupDistinguishedName)
    {
        using var user = RequireEntry(identity, "user");
        using var group = Open($"LDAP://{groupDistinguishedName}");
        group.Properties["member"].Remove(user.Properties["distinguishedName"].Value);
        group.CommitChanges();
    }

    public bool OrganizationalUnitExists(string organizationalUnitDn)
    {
        try
        {
            using var entry = Open($"LDAP://{organizationalUnitDn}");
            var objectClass = entry.Properties["objectClass"];
            foreach (var value in objectClass)
            {
                if (string.Equals(value?.ToString(), "organizationalUnit", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    // ---- Computers ----

    public AdComputerInfo? GetComputer(string identity)
    {
        using var entry = FindEntry(identity, "computer");
        return entry is null ? null : MapComputer(entry);
    }

    public void SetComputerEnabled(string identity, bool enabled)
    {
        using var entry = RequireEntry(identity, "computer");
        var uac = entry.Properties["userAccountControl"].Value is int existingUac ? existingUac : 0x1000;
        uac = enabled ? uac & ~0x2 : uac | 0x2;
        entry.Properties["userAccountControl"].Value = uac;
        entry.CommitChanges();
    }

    public void SetComputerDescription(string identity, string? description) =>
        SetOrClearAttribute(identity, "computer", "description", description);

    public void MoveComputer(string identity, string targetOrganizationalUnitDn)
    {
        using var entry = RequireEntry(identity, "computer");
        using var newParent = Open($"LDAP://{targetOrganizationalUnitDn}");
        entry.MoveTo(newParent);
    }

    // ---- Groups / OUs ----

    public string? GetGroupSid(string groupName)
    {
        using var entry = FindEntry(groupName, "group");
        if (entry?.Properties["objectSid"].Value is not byte[] sidBytes)
        {
            return null;
        }
        return new SecurityIdentifier(sidBytes, 0).Value;
    }

    /// <summary>
    /// SIDs of every group the named group is a member of TRANSITIVELY (its parent groups up the
    /// whole nesting chain), never including the group itself. Closes the privilege-nesting
    /// bypass a direct SID/RID check on the target group alone would miss: an ordinary-looking
    /// group that is itself a member of Domain Admins (directly or several levels up) confers
    /// Domain Admin, yet its own SID is not a privileged RID. Uses AD's LDAP_MATCHING_RULE_IN_CHAIN
    /// ("member:1.2.840.113556.1.4.1941:") so the whole chain is evaluated by the DC in one query.
    /// Best-effort: returns an empty list (never throws) if the group has no parents or can't be
    /// resolved, so a transient AD hiccup can never block an ordinary group grant - the direct
    /// check on the target group stays the primary, fail-closed gate.
    /// </summary>
    public IReadOnlyList<string> GetGroupParentGroupSids(string groupName)
    {
        var result = new List<string>();
        try
        {
            using var group = FindEntry(groupName, "group");
            var dn = group?.Properties["distinguishedName"].Value?.ToString();
            if (string.IsNullOrEmpty(dn))
            {
                return result;
            }
            using var root = OpenRoot();
            using var searcher = new DirectorySearcher(root)
            {
                Filter = $"(&(objectClass=group)(member:1.2.840.113556.1.4.1941:={EscapeLdapFilterValue(dn)}))",
                PageSize = 1000,
            };
            searcher.PropertiesToLoad.Add("objectSid");
            using var found = searcher.FindAll();
            foreach (SearchResult r in found)
            {
                if (r.Properties["objectSid"].Count > 0 && r.Properties["objectSid"][0] is byte[] sidBytes)
                {
                    result.Add(new SecurityIdentifier(sidBytes, 0).Value);
                }
            }
        }
        catch
        {
            // Supplemental defense-in-depth only - swallow and fall back to the direct check.
        }
        return result;
    }

    public IReadOnlyList<string> ListPrivilegedUserSamAccountNames(string? domainSid)
    {
        using var root = OpenRoot();
        var groupDns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sid in PrivilegedGroupPolicy.GetPrivilegedGroupSids(domainSid))
        {
            var sidBytes = new byte[new SecurityIdentifier(sid).BinaryLength];
            new SecurityIdentifier(sid).GetBinaryForm(sidBytes, 0);
            var sidFilter = string.Concat(sidBytes.Select(b => $"\\{b:x2}"));
            using var searcher = new DirectorySearcher(root) { Filter = $"(&(objectClass=group)(objectSid={sidFilter}))" };
            searcher.PropertiesToLoad.Add("distinguishedName");
            if (searcher.FindOne() is { } found)
            {
                groupDns.Add(GetProp(found, "distinguishedName"));
            }
        }
        foreach (var name in PrivilegedGroupPolicy.PrivilegedGroupNames)
        {
            using var group = FindEntry(name, "group");
            if (group?.Properties["distinguishedName"].Value?.ToString() is { Length: > 0 } dn)
            {
                groupDns.Add(dn);
            }
        }

        // memberOf-in-chain covers direct and nested membership; primaryGroupID covers the rare
        // account whose primary group was switched to a privileged domain group (that membership
        // never appears in member/memberOf).
        var clauses = groupDns.Select(dn => $"(memberOf:1.2.840.113556.1.4.1941:={EscapeLdapFilterValue(dn)})").ToList();
        if (!string.IsNullOrWhiteSpace(domainSid))
        {
            clauses.AddRange(PrivilegedGroupPolicy.PrivilegedGroupRids.Select(rid => $"(primaryGroupID={rid})"));
        }
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (clauses.Count == 0)
        {
            return result.ToList();
        }
        using (var userSearcher = new DirectorySearcher(root)
        {
            Filter = $"(&(objectClass=user)(objectCategory=person)(|{string.Concat(clauses)}))",
            PageSize = 1000,
        })
        {
            userSearcher.PropertiesToLoad.Add("sAMAccountName");
            using var found = userSearcher.FindAll();
            foreach (SearchResult r in found)
            {
                result.Add(GetProp(r, "sAMAccountName"));
            }
        }
        result.Remove("");
        return result.ToList();
    }

    /// <summary>A blank/whitespace term lists every group in the domain (no name filter at all,
    /// rather than relying on an empty-substring wildcard) - GroupPickerDialog uses this to load
    /// the full picklist once up front instead of requiring a search term before showing anything.</summary>
    public IReadOnlyList<AdGroupSummary> SearchGroupsByName(string term)
    {
        var trimmed = term.Trim();
        var filter = string.IsNullOrEmpty(trimmed)
            ? "(objectClass=group)"
            : $"(&(objectClass=group)(name=*{EscapeLdapFilterValue(trimmed)}*))";
        using var root = OpenRoot();
        using var searcher = new DirectorySearcher(root)
        {
            Filter = filter,
            PageSize = 1000,
        };
        searcher.PropertiesToLoad.Add("name");
        searcher.PropertiesToLoad.Add("distinguishedName");
        searcher.PropertiesToLoad.Add("objectSid");
        var results = new List<AdGroupSummary>();
        using var found = searcher.FindAll();
        foreach (SearchResult result in found)
        {
            results.Add(new AdGroupSummary(GetProp(result, "name"), GetProp(result, "distinguishedName"), GetSidProp(result, "objectSid")));
        }
        return results;
    }

    /// <summary>Like GetProp, but for a binary objectSid attribute - returns the SID's string form
    /// (e.g. "S-1-5-21-...-512") or null if the property is missing/not a byte array.</summary>
    private static string? GetSidProp(SearchResult result, string name) =>
        result.Properties[name].Count > 0 && result.Properties[name][0] is byte[] sidBytes
            ? new SecurityIdentifier(sidBytes, 0).Value
            : null;

    public IReadOnlyList<AdOrganizationalUnitInfo> ListOrganizationalUnits()
    {
        using var root = OpenRoot();
        using var searcher = new DirectorySearcher(root) { Filter = "(objectClass=organizationalUnit)", PageSize = 1000 };
        searcher.PropertiesToLoad.Add("name");
        searcher.PropertiesToLoad.Add("distinguishedName");
        var results = new List<AdOrganizationalUnitInfo>();
        using var found = searcher.FindAll();
        foreach (SearchResult result in found)
        {
            results.Add(new AdOrganizationalUnitInfo(GetProp(result, "name"), GetProp(result, "distinguishedName")));
        }
        return results;
    }

    public IReadOnlyList<AdUserMatchInfo> ListAllUsers()
    {
        using var root = OpenRoot();
        using var searcher = new DirectorySearcher(root)
        {
            Filter = "(&(objectClass=user)(objectCategory=person))",
            PageSize = 1000,
        };
        foreach (var prop in new[] { "sAMAccountName", "givenName", "sn", "userAccountControl" })
        {
            searcher.PropertiesToLoad.Add(prop);
        }
        var results = new List<AdUserMatchInfo>();
        using var found = searcher.FindAll();
        foreach (SearchResult result in found)
        {
            var uac = result.Properties["userAccountControl"].Count > 0 ? (int)result.Properties["userAccountControl"][0] : 0;
            results.Add(new AdUserMatchInfo(GetProp(result, "sAMAccountName"), GetProp(result, "givenName"), GetProp(result, "sn"), (uac & 0x2) == 0));
        }
        return results;
    }

    public IReadOnlyList<AdComputerMatchInfo> ListComputersInOu(string organizationalUnitDn)
    {
        using var ouRoot = Open($"LDAP://{organizationalUnitDn}");
        using var searcher = new DirectorySearcher(ouRoot)
        {
            Filter = "(objectClass=computer)",
            PageSize = 1000,
        };
        foreach (var prop in new[] { "name", "distinguishedName", "userAccountControl" })
        {
            searcher.PropertiesToLoad.Add(prop);
        }
        var results = new List<AdComputerMatchInfo>();
        using var found = searcher.FindAll();
        foreach (SearchResult result in found)
        {
            var uac = result.Properties["userAccountControl"].Count > 0 ? (int)result.Properties["userAccountControl"][0] : 0;
            results.Add(new AdComputerMatchInfo(GetProp(result, "name"), GetProp(result, "distinguishedName"), (uac & 0x2) == 0));
        }
        return results;
    }

    public IReadOnlyList<AdUserChangeInfo> GetRecentlyChangedUsers(int days)
    {
        var cutoff = DateTime.UtcNow.AddDays(-days).ToString("yyyyMMddHHmmss.0Z");
        using var root = OpenRoot();
        using var searcher = new DirectorySearcher(root)
        {
            Filter = $"(&(objectClass=user)(objectCategory=person)(whenChanged>={cutoff}))",
            PageSize = 1000,
        };
        foreach (var prop in new[] { "givenName", "sn", "userAccountControl", "whenChanged", "whenCreated" })
        {
            searcher.PropertiesToLoad.Add(prop);
        }
        var results = new List<AdUserChangeInfo>();
        using var found = searcher.FindAll();
        foreach (SearchResult result in found)
        {
            var uac = result.Properties["userAccountControl"].Count > 0 ? (int)result.Properties["userAccountControl"][0] : 0;
            var whenChanged = result.Properties["whenChanged"].Count > 0 ? (DateTime?)result.Properties["whenChanged"][0] : null;
            var whenCreated = result.Properties["whenCreated"].Count > 0 ? (DateTime?)result.Properties["whenCreated"][0] : null;
            results.Add(new AdUserChangeInfo(GetProp(result, "givenName"), GetProp(result, "sn"), (uac & 0x2) == 0, whenChanged, whenCreated));
        }
        return results.OrderByDescending(u => u.WhenChanged).ToList();
    }

    public bool SamAccountNameExists(string samAccountName)
    {
        using var root = OpenRoot();
        using var searcher = new DirectorySearcher(root) { Filter = $"(sAMAccountName={EscapeLdapFilterValue(samAccountName)})" };
        return searcher.FindOne() is not null;
    }

    // ---- Helpers ----

    /// <summary>
    /// Security note: this deliberately NEVER treats <paramref name="identity"/> as a bind target,
    /// even if it happens to be shaped like a distinguished name - it always resolves through an
    /// escaped LDAP search instead. An earlier version short-circuited to a direct
    /// `Open($"LDAP://{identity}")` bind whenever the string merely looked like a DN (contained
    /// "DC=" and "="), which meant a Username field value that was never actually validated -
    /// reachable via CSV import (no format check on that column) or a hand-edited shared
    /// inventory.json - could silently redirect every AD action here (Transfer, Offboard, Check
    /// AD, Match User) onto an attacker-chosen object instead of the intended user. The one
    /// legitimate "I already have a trusted DN" case (resolving a Manager's DN, itself fetched
    /// from a real prior AD read) now goes through the separate, explicitly-named
    /// GetUserByDistinguishedName instead, so there is no ambiguous path left for untrusted data
    /// to reach a raw bind.
    /// </summary>
    // ---- Copy User Permissions ----

    public string? GetUserSid(string identity)
    {
        using var entry = FindEntry(identity, "user");
        return entry?.Properties["objectSid"].Value is byte[] sidBytes
            ? new SecurityIdentifier(sidBytes, 0).Value
            : null;
    }

    public AdGroupInfo? GetGroupByDistinguishedName(string groupDistinguishedName)
    {
        try
        {
            using var entry = Open($"LDAP://{EscapeAdsiPath(groupDistinguishedName)}");
            string Get(string prop) => entry.Properties[prop].Value?.ToString() ?? "";
            var sid = entry.Properties["objectSid"].Value is byte[] sidBytes ? new SecurityIdentifier(sidBytes, 0).Value : null;
            // groupType bit flags: 0x80000000 = security-enabled; 0x2 global, 0x4 domain local, 0x8 universal.
            var groupType = entry.Properties["groupType"].Value is int gt ? gt : 0;
            var scope = (groupType & 0x8) != 0 ? "Universal" : (groupType & 0x4) != 0 ? "DomainLocal" : "Global";
            return new AdGroupInfo(Get("sAMAccountName"), Get("name"), Get("distinguishedName"), sid,
                IsSecurityGroup: (groupType & unchecked((int)0x80000000)) != 0, scope);
        }
        catch
        {
            return null;
        }
    }

    public void AddUserToGroupByDistinguishedName(string identity, string groupDistinguishedName)
    {
        using var user = RequireEntry(identity, "user");
        using var group = Open($"LDAP://{EscapeAdsiPath(groupDistinguishedName)}");
        group.Properties["member"].Add(user.Properties["distinguishedName"].Value);
        group.CommitChanges();
    }

    /// <summary>ADSI treats "/" in an LDAP:// path as a separator, so a DN containing one (legal in
    /// a CN, e.g. "CN=HR/Payroll,...") must have it escaped before binding.</summary>
    private static string EscapeAdsiPath(string distinguishedName) => distinguishedName.Replace("/", "\\/");

    private DirectoryEntry? FindEntry(string identity, string objectClass)
    {
        using var root = OpenRoot();
        var safeIdentity = EscapeLdapFilterValue(identity);
        // Computer objects' sAMAccountName always carries a trailing "$" (e.g. "360-L04$" for a
        // computer literally named "360-L04") - matching on the plain "name" attribute instead
        // is what lets a bare device name like the PowerShell ActiveDirectory module's own
        // Get-ADComputer -Identity resolve without the caller needing to know about the "$".
        var identityFilter = objectClass == "computer"
            ? $"(|(name={safeIdentity})(sAMAccountName={safeIdentity}$))"
            : $"(sAMAccountName={safeIdentity})";
        using var searcher = new DirectorySearcher(root)
        {
            Filter = $"(&(objectClass={objectClass}){identityFilter})",
        };
        searcher.PropertiesToLoad.Add("distinguishedName");
        var result = searcher.FindOne();
        return result?.GetDirectoryEntry();
    }

    private DirectoryEntry RequireEntry(string identity, string objectClass) =>
        FindEntry(identity, objectClass) ?? throw new InvalidOperationException($"'{identity}' ({objectClass}) was not found in Active Directory.");

    private void SetOrClearAttribute(string identity, string objectClass, string attribute, string? value)
    {
        using var entry = RequireEntry(identity, objectClass);
        if (string.IsNullOrEmpty(value))
        {
            if (entry.Properties.Contains(attribute))
            {
                entry.Properties[attribute].Clear();
            }
        }
        else
        {
            entry.Properties[attribute].Value = value;
        }
        entry.CommitChanges();
    }

    private static AdUserInfo MapUser(DirectoryEntry entry)
    {
        string Get(string prop) => entry.Properties[prop].Value?.ToString() ?? "";
        var uac = entry.Properties["userAccountControl"].Value is int i ? i : 0;
        var managerDn = Get("manager");
        return new AdUserInfo(
            SamAccountName: Get("sAMAccountName"),
            DistinguishedName: Get("distinguishedName"),
            GivenName: Get("givenName"),
            Surname: Get("sn"),
            Enabled: (uac & 0x2) == 0,
            ManagerDistinguishedName: string.IsNullOrEmpty(managerDn) ? null : managerDn,
            Description: Get("description"),
            Office: Get("physicalDeliveryOfficeName"),
            Info: Get("info"));
    }

    private static AdComputerInfo MapComputer(DirectoryEntry entry)
    {
        string Get(string prop) => entry.Properties[prop].Value?.ToString() ?? "";
        var uac = entry.Properties["userAccountControl"].Value is int i ? i : 0;
        return new AdComputerInfo(
            Name: Get("name"),
            DistinguishedName: Get("distinguishedName"),
            Enabled: (uac & 0x2) == 0,
            Description: Get("description"));
    }

    private static string GetProp(SearchResult result, string name) =>
        result.Properties[name].Count > 0 ? result.Properties[name][0]?.ToString() ?? "" : "";

    /// <summary>Escapes an LDAP search filter value per RFC 4515 - never build a filter by
    /// direct string concatenation of user/AD-sourced input without this.</summary>
    private static string EscapeLdapFilterValue(string value) => value
        .Replace("\\", "\\5c")
        .Replace("*", "\\2a")
        .Replace("(", "\\28")
        .Replace(")", "\\29")
        .Replace("\0", "\\00");

    /// <summary>Escapes a DN component (the part after "CN=") per RFC 4514.</summary>
    private static string EscapeDnComponent(string value)
    {
        var escaped = value
            .Replace("\\", "\\\\")
            .Replace(",", "\\,")
            .Replace("+", "\\+")
            .Replace("\"", "\\\"")
            .Replace("<", "\\<")
            .Replace(">", "\\>")
            .Replace(";", "\\;")
            .Replace("\0", "\\00");
        // RFC 4514: a leading '#' or space, and a trailing space, must also be escaped or the
        // RDN is malformed - e.g. a display name like "#Ops" or one with stray edge whitespace.
        if (escaped.StartsWith(' ') || escaped.StartsWith('#'))
        {
            escaped = "\\" + escaped;
        }
        if (escaped.EndsWith(' '))
        {
            escaped = escaped[..^1] + "\\ ";
        }
        return escaped;
    }
}
