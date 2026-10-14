namespace InventoryManager.Core.Onboarding;

/// <summary>Everything the wizard collected, handed to OnboardingService in one call - mirrors
/// the PowerShell build's $doFinish scriptblock reading every step's controls at once.</summary>
public sealed record OnboardingRequest(
    string TicketNumber,
    string FirstName,
    string LastName,
    string Description,
    string Office,
    string JobTitle,
    string Department,
    string Company,
    string SamAccountName,
    string UpnSuffix,
    string Password,
    bool MustChangePasswordAtLogon,
    string OrganizationalUnitDn,
    string ManagerDistinguishedName,
    string ManagerDisplay,
    IReadOnlyList<string> GroupNames,
    string ComputerName,
    bool ComputerFoundInAd,
    string SuUsername)
{
    /// <summary>Every account this wizard creates gets the same logon script - Francois,
    /// 2026-09-16. Not exposed as a wizard field (there's only ever one value in practice), but
    /// kept as a named constant here rather than hardcoded inside AdService so the one place that
    /// might ever need to change it is obvious.</summary>
    public const string DefaultLogonScript = "logon.bat";
}

public sealed record OnboardingGroupOutcome(string GroupName, bool Added, string Reason);

public sealed record OnboardingResult(
    Ad.AdUserInfo CreatedUser,
    string FullName,
    IReadOnlyList<OnboardingGroupOutcome> GroupOutcomes,
    bool ComputerDescriptionSynced);

/// <summary>Thrown by OnboardingService for a hard-stop that must abort before any AD write -
/// mirrors the PowerShell build's own pre-flight MessageBox-and-return checks in $doFinish.</summary>
public sealed class OnboardingBlockedException(string message) : Exception(message);

public static class OnboardingMarker
{
    /// <summary>Silently marks every AD user this tool creates, for later incident-response
    /// correlation - NOT shown anywhere in this app's own UI or ADUC's normal tabs, only visible
    /// via the Attribute Editor tab. "comment" is a base AD schema attribute present on every
    /// user object regardless of Exchange schema state (this forest has none).</summary>
    public const string Attribute = "comment";
    public const string Value = "InventoryAccountCreated";
}
