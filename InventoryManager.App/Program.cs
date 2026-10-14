using System.Text.Json;
using InventoryManager.App.Login;
using InventoryManager.Core.Ad;
using InventoryManager.Core.Security;

namespace InventoryManager.App;

static class Program
{
    private const string AdDomainFqdn = "contoso.local";
    private const string InventoryGateAccountSam = "inv-gateaccount";
    private const string SuGroupName = "InventoryManager";
    private const int MaxLoginAttempts = 3;

    /// <summary>
    /// Francois, 2026-09-22: after several rounds of remote troubleshooting of the
    /// INVMGR_DATA_DIR/INVMGR_SECURITY_LOG_DIR rollout (screenshots of PowerShell checks back and
    /// forth), asked for a way to diagnose this without needing anyone to manually copy/paste log
    /// data. Deliberately a FIXED, hardcoded path (not configurable, not derived from either env
    /// var this is meant to diagnose) - a diagnostic breadcrumb trail has to reach a location that
    /// works independently of the very configuration it's investigating, or it would silently fail
    /// exactly when it's most needed. Every launch appends one line here, success or failure,
    /// including what the raw env vars actually looked like to THIS process specifically (Process
    /// scope - what the app itself sees) versus a live Machine-scope registry read, so the "Explorer
    /// hasn't refreshed its own environment yet" class of bug is visible directly, without needing
    /// anyone to run a diagnostic command and report back manually.
    /// </summary>
    private const string StartupDiagnosticsPath = @"\\fileserver01\IT-Apps\InventoryAppData\startup-diagnostics.jsonl";

    /// <summary>
    ///  The main entry point for the application.
    /// </summary>
    [STAThread]
    static void Main()
    {
        // To customize application configuration such as set high DPI settings or default font,
        // see https://aka.ms/applicationconfiguration.
        ApplicationConfiguration.Initialize();

        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "InventoryManagerNet");
        Directory.CreateDirectory(appDataDir);

        // Francois, 2026-09-18: a second admin logging in for the first time saw an empty
        // inventory (nothing in any tab, even after AD Sync) and their own login didn't show up in
        // the Security Log. Root cause found: dataFilePath had NO shared-location override at all
        // (unlike the security log dir below, which already had one) - it was ALWAYS
        // %LocalAppData%\InventoryManagerNet\inventory.json, a per-user, per-machine path. Every
        // admin's install was silently reading/writing its own separate, empty local file - the
        // multi-user concurrent-save engine in InventoryStore has been correct and tested since
        // Phase 1, but two installs pointed at two different files can never see each other's data
        // no matter how good that engine is. Same fix pattern as INVMGR_SECURITY_LOG_DIR below:
        // configurable via a machine env var, falls back to local if unset/unreachable so a
        // misconfigured or down share never blocks the app from launching. Backups and the
        // Activity Log folder are derived from this same directory (see MainForm's constructor),
        // so pointing this at a shared location moves all three together.
        var (dataDir, dataDirWarning) = ResolveSharedDir("INVMGR_DATA_DIR", appDataDir);
        var dataFilePath = Path.Combine(dataDir, "inventory.json");

        // Tamper-evident security ledger + alert sink, created before the login gate so even a
        // failed/again-failed logon is recorded. Deployment note: for real assurance the ledger and
        // alert file should live where operators can append but not rewrite (restrictive ACLs), or
        // be shipped to a SIEM - the hash chain makes edits DETECTABLE, ACLs make them impossible.
        // Where the tamper-evident ledger + alert file live. Configurable via the machine
        // environment variable INVMGR_SECURITY_LOG_DIR (set it by GPO to an append-only share so
        // operators can't rewrite their own audit trail - see the hardening runbook). Falls back
        // to per-user LocalAppData if it's unset, unreachable, or not writable, so a misconfigured
        // or down share never blocks the app from launching or from recording *something*.
        var (securityLogDir, securityLogDirWarning) = ResolveSharedDir("INVMGR_SECURITY_LOG_DIR", appDataDir);
        var securityLog = new SecurityEventLog(
            Path.Combine(securityLogDir, "security-events.jsonl"),
            new FileAlertSink(Path.Combine(securityLogDir, "security-alerts.log")));

        WriteStartupDiagnostics(dataDir, dataDirWarning, securityLogDir, securityLogDirWarning);

        // Francois, 2026-09-22: a shared-location misconfiguration on one specific machine ("this
        // isn't working for another IT staff") was invisible until now - ResolveSharedDir silently
        // falls back to local on ANY failure (by design, so a down/misconfigured share never blocks
        // startup), but that meant nobody sitting at the affected machine ever saw WHY it fell back,
        // and neither could I from here. Surface it loudly, once, right at startup - this is the one
        // thing standing between "the app just quietly doesn't share data" and "here's the exact
        // reason, go fix that."
        var sharedDirWarnings = new[] { dataDirWarning, securityLogDirWarning }.Where(w => w is not null).ToList();
        if (sharedDirWarnings.Count > 0)
        {
            MessageBox.Show(
                "This machine has a shared-location environment variable configured, but it could not be used - falling back to local storage for this launch, which means this session will NOT see other admins' inventory/security data.\r\n\r\n"
                + string.Join("\r\n\r\n", sharedDirWarnings)
                + "\r\n\r\nCommon causes: the share is unreachable from this machine/network, or this account doesn't have write access to it. Contact IT if this persists.",
                "Inventory Manager - Shared Location Unavailable", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        // Separate, unbounded, tamper-evident ledger of every inventory field change (Francois,
        // 2026-09-15: "I pretty much want a tracked event log for every user modified and by who").
        // Same directory/hash-chain mechanism as the security ledger above, but its own file - this
        // is routine business activity being logged for audit purposes, not a security signal, so
        // it doesn't share a file with (or alert like) actual security events.
        var inventoryChangeLog = new SecurityEventLog(Path.Combine(securityLogDir, "inventory-changes.jsonl"));

        // Two independent AD checks, mirroring the PowerShell build's Invoke-AccessGate/
        // Invoke-SuLogin: the gate account (a passphrase check - "is this operator allowed to even
        // launch the tool at all"), then the operator's own AD login gated on SU-group membership.
        // Neither check needs a credential override to run - ValidateCredentials/
        // IsUserInGroupTransitive query AD as the current Windows/machine identity regardless of
        // whose credentials are being validated.
        var gateCheckService = new AdService(AdDomainFqdn);

        if (!LoginGate.RunAccessGate(gateCheckService, AdDomainFqdn, InventoryGateAccountSam, MaxLoginAttempts, securityLog))
        {
            MessageBox.Show("Access gate check failed or was cancelled. Inventory Manager will now close.", "Inventory Manager", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var suLogin = LoginGate.RunSuLogin(gateCheckService, AdDomainFqdn, SuGroupName, MaxLoginAttempts, securityLog);
        if (suLogin is null)
        {
            MessageBox.Show("Admin login failed or was cancelled. Inventory Manager will now close.", "Inventory Manager", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        // From here on, every AD write the app makes runs as the SU's own account, never as
        // whoever happens to be logged into Windows.
        var adService = new AdService(AdDomainFqdn, suLogin.Credentials);

        Application.Run(new MainForm(dataFilePath, adService, suLogin.Username, AdDomainFqdn, securityLog, inventoryChangeLog));
    }

    /// <summary>
    /// Resolves a directory from a machine environment variable when it points at a directory this
    /// process can actually create and write to; otherwise returns <paramref name="fallbackDir"/>
    /// (per-user LocalAppData). Shared by INVMGR_SECURITY_LOG_DIR (set it by GPO to an append-only
    /// share so operators can't rewrite their own audit trail - see the hardening runbook) and
    /// INVMGR_DATA_DIR (set it to a shared network location so every admin's install reads/writes
    /// the same inventory.json - see the comment above dataDir in Main). The write probe matters
    /// because the whole point of a configured location is usually a locked-down share - if it's
    /// unreachable or the ACLs are wrong, we must degrade to local rather than throw on every
    /// write, or blocking startup entirely just because one env var points at a bad path.
    /// Returns a Warning alongside the resolved directory - non-null only when the env var WAS set
    /// but couldn't actually be used (the interesting, "something is misconfigured" case), never
    /// when it's simply unset (the normal case for a machine that hasn't been configured at all).
    /// </summary>
    private static (string Dir, string? Warning) ResolveSharedDir(string envVarName, string fallbackDir)
    {
        var configured = Environment.GetEnvironmentVariable(envVarName);
        if (string.IsNullOrWhiteSpace(configured))
        {
            return (fallbackDir, null);
        }
        try
        {
            Directory.CreateDirectory(configured);
            var probe = Path.Combine(configured, $".writeprobe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return (configured, null);
        }
        catch (Exception ex)
        {
            // Unset-and-reachable is the happy path above; here the share is misconfigured, down,
            // or read-only for this operator. Fall back so the app still runs and still works, but
            // tell the caller exactly why, so it can be surfaced instead of silently swallowed.
            return (fallbackDir, $"{envVarName} is set to '{configured}', but this session could not write to it: {ex.Message}");
        }
    }

    /// <summary>Appends one diagnostic line to StartupDiagnosticsPath for every launch - see that
    /// constant's own comment for why the destination is fixed rather than configurable. Records
    /// both the Process-scope env var value (exactly what THIS app process itself sees, via the
    /// same no-target overload ResolveSharedDir uses) and a live Machine-scope registry read, side
    /// by side - a mismatch between the two is the specific, previously invisible symptom of "the
    /// registry is right but this process's own environment is stale" (e.g. launched from an
    /// Explorer session that hasn't been refreshed since the machine env var was set). Best-effort
    /// and completely silent on failure - a diagnostics write must never affect startup, and an
    /// unreachable share here is itself just another (recorded, where possible) data point, not an
    /// error worth interrupting anyone over.</summary>
    private static void WriteStartupDiagnostics(string dataDir, string? dataDirWarning, string securityLogDir, string? securityLogDirWarning)
    {
        try
        {
            var entry = new
            {
                ts = DateTime.UtcNow.ToString("o"),
                machine = Environment.MachineName,
                domain = Environment.UserDomainName,
                user = Environment.UserName,
                appVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown",
                dataDir_envProcess = Environment.GetEnvironmentVariable("INVMGR_DATA_DIR"),
                dataDir_envMachine = Environment.GetEnvironmentVariable("INVMGR_DATA_DIR", EnvironmentVariableTarget.Machine),
                dataDir_resolved = dataDir,
                dataDir_warning = dataDirWarning,
                securityLogDir_envProcess = Environment.GetEnvironmentVariable("INVMGR_SECURITY_LOG_DIR"),
                securityLogDir_envMachine = Environment.GetEnvironmentVariable("INVMGR_SECURITY_LOG_DIR", EnvironmentVariableTarget.Machine),
                securityLogDir_resolved = securityLogDir,
                securityLogDir_warning = securityLogDirWarning,
            };
            var dir = Path.GetDirectoryName(StartupDiagnosticsPath);
            if (dir is not null)
            {
                Directory.CreateDirectory(dir);
            }
            File.AppendAllText(StartupDiagnosticsPath, JsonSerializer.Serialize(entry) + Environment.NewLine);
        }
        catch
        {
            // Nowhere to report a failure of the diagnostics mechanism itself without becoming the
            // very noise it exists to avoid - if the share is unreachable, this line simply doesn't
            // land, which is itself informative once a launch DOES succeed in writing one.
        }
    }
}
