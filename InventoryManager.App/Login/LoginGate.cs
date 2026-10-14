using InventoryManager.Core.Ad;
using InventoryManager.Core.Security;

namespace InventoryManager.App.Login;

/// <summary>
/// WinForms dialog loop for the two-layer login gate - the decision logic itself
/// (LoginGateService, in Core) is unit-tested; this class is just the retry-loop-plus-MessageBox
/// wiring around it, matching the PowerShell build's Invoke-AccessGate/Invoke-SuLogin shape.
///
/// One deliberate simplification from the PowerShell build: there, the access gate ran before the
/// main window existed and the SU login ran afterward (layered on top of the already-visible
/// window). Here, both run before MainForm is ever constructed. Neither check depends on the main
/// window existing, so this is functionally equivalent - just simpler than giving MainForm a
/// mutable post-construction credential swap for no real benefit.
/// </summary>
public static class LoginGate
{
    public static bool RunAccessGate(IAdService adService, string domain, string gateAccountSam, int maxAttempts, ISecurityAudit? audit = null)
    {
        audit ??= NullSecurityAudit.Instance;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var dlg = new CredentialPromptDialog(
                "Inventory Manager - Access Gate",
                "This tool is restricted. Enter the password for the designated gate account to continue.",
                gateAccountSam);
            if (dlg.ShowDialog() != DialogResult.OK || dlg.Result is not { } cred)
            {
                return false;
            }
            if (LoginGateService.CheckGateAccount(adService, domain, cred.Username, cred.Password))
            {
                return true;
            }
            audit.Record(SecurityEventType.GateFailure, SecuritySeverity.Warning, gateAccountSam, "",
                $"Incorrect gate-account password (attempt {attempt} of {maxAttempts}).");
            MessageBox.Show($"Incorrect password. Attempt {attempt} of {maxAttempts}.", "Access Denied", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        return false;
    }

    public sealed record SuLoginResult(string Username, AdCredentialContext Credentials);

    public static SuLoginResult? RunSuLogin(IAdService adService, string domain, string suGroupName, int maxAttempts, ISecurityAudit? audit = null)
    {
        audit ??= NullSecurityAudit.Instance;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var dlg = new CredentialPromptDialog(
                "Inventory Manager - Admin Login",
                $"Enter your own AD admin account credentials. Your account must be a member of '{suGroupName}' to use this tool.",
                fixedUsername: null);
            if (dlg.ShowDialog() != DialogResult.OK || dlg.Result is not { } cred)
            {
                return null;
            }

            var attemptResult = LoginGateService.AttemptSuLogin(adService, domain, suGroupName, cred.Username, cred.Password);
            switch (attemptResult.Outcome)
            {
                case SuLoginAttemptOutcome.EmptyUsername:
                    MessageBox.Show("Username is required.", "Access Denied", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    continue;
                case SuLoginAttemptOutcome.InvalidCredentials:
                    audit.Record(SecurityEventType.LoginFailure, SecuritySeverity.Warning, cred.Username, "",
                        $"Invalid SU credentials (attempt {attempt} of {maxAttempts}).");
                    MessageBox.Show($"Incorrect username or password. Attempt {attempt} of {maxAttempts}.", "Access Denied", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    continue;
                case SuLoginAttemptOutcome.NotInSuGroup:
                    // Valid AD account trying to use an admin tool it's not entitled to - notable.
                    audit.Record(SecurityEventType.LoginFailure, SecuritySeverity.Warning, cred.Username, suGroupName,
                        $"Valid AD account '{cred.Username}' is not a member of '{suGroupName}' - access denied.");
                    MessageBox.Show($"'{cred.Username}' is a valid AD account but is not a member of '{suGroupName}'. Access denied.", "Access Denied", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    continue;
                case SuLoginAttemptOutcome.Success:
                    audit.Record(SecurityEventType.LoginSuccess, SecuritySeverity.Info, cred.Username, "",
                        "SU login succeeded.");
                    return new SuLoginResult(cred.Username, AdCredentialContext.FromPlainText(attemptResult.QualifiedUsername!, cred.Password, domain));
            }
        }
        return null;
    }
}
