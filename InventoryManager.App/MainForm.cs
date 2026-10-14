using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using InventoryManager.App.Dialogs;
using InventoryManager.App.Grid;
using InventoryManager.App.Onboarding;
using InventoryManager.App.Permissions;
using InventoryManager.App.Transfer;
using InventoryManager.Core;
using InventoryManager.Core.Ad;
using InventoryManager.Core.CellPhones;
using InventoryManager.Core.Security;
using InventoryManager.Core.Logging;
using InventoryManager.Core.Models;
using InventoryManager.Core.Offboarding;
using InventoryManager.Core.Persistence;
using InventoryManager.Core.Transfer;

namespace InventoryManager.App;

/// <summary>
/// The full C# rewrite: a working tabbed inventory grid (Add/Edit/Delete, per-tab search,
/// periodic cross-session refresh) backed by the ported InventoryStore, plus the Onboarding
/// Wizard, Transfer User, and Offboarding. The two-layer login gate (Program.Main's
/// LoginGate.RunAccessGate/RunSuLogin) has already run by the time this form is constructed -
/// the AdService passed in is authenticated as the SU's own AD account, and suUsername is that
/// same operator's real username, used everywhere an AD Notes entry needs to stamp "who did this."
/// </summary>
/// <summary>A single "Move to" destination - either a real Status move, or (when
/// <see cref="DomainValue"/> is set) a Domain-field move, matching the PowerShell build's
/// $DomainMoveTarget concept (Off Domain / On Domain (CONTOSO), which set Domain instead of Status).</summary>
public sealed record MoveTarget(string Label, string? StatusKey, string? DomainValue)
{
    public bool IsDomainMove => DomainValue is not null;
}

public sealed class MainForm : Form
{
    private const string OffDomainTabKey = "OffDomain";
    private const string OnboardingUpnSuffix = "contoso.com";
    // Ships alongside InventoryManager.App.exe (see the <None>/CopyToOutputDirectory entry in
    // InventoryManager.App.csproj) so every admin gets it automatically with the app itself - no
    // separate manual install step, and no dependency on Program Files or any other fixed
    // install location that doesn't exist yet (there's no installer). The real defense against a
    // tampered copy is the signature check in LaunchOffboardingScript (ScriptSignatureVerifier:
    // valid Authenticode signature AND signer pinned to the Contoso IT Scripts cert) - the app refuses
    // to launch the file otherwise (it used to rely on -ExecutionPolicy AllSigned, replaced
    // 2026-10-08, see LaunchOffboardingScript). Francois
    // signed it 2026-09-15 with a self-signed "CN=Contoso IT Scripts" cert, trusted on this machine's
    // Root/TrustedPublisher stores (confirmed via Get-AuthenticodeSignature: Status=Valid) - the
    // same certificate needs importing on every OTHER admin's machine (or replaced with a real
    // internal AD CS-issued code-signing cert, which every domain-joined machine already trusts)
    // before this will run for them too.
    private static readonly string OffboardingScriptPath = Path.Combine(AppContext.BaseDirectory, "Offboarding2026.ps1");
    // Same "ships with the app, no separate install step" reasoning as OffboardingScriptPath above
    // - see InventoryManager.App.csproj's CopyToOutputDirectory entry for this file.
    private static readonly string DocumentationPath = Path.Combine(AppContext.BaseDirectory, "docs", "inventory-manager-guide.html");
    private const string ComputersOuDn = "OU=Domain Computers,DC=contoso,DC=local";
    private const string DisabledComputersOuDn = "OU=Disabled Computers,OU=Domain Computers,DC=contoso,DC=local";
    // Confirmed against real AD 2026-09-16 (Get-ADOrganizationalUnit under Domain Computers) -
    // "Laptop" (singular), not "Laptops".
    private const string LaptopOuDn = "OU=Laptop,OU=Domain Computers,DC=contoso,DC=local";
    private const int MaxBackups = 5;
    private static readonly TimeSpan BackupInterval = TimeSpan.FromMinutes(5);
    private const int LogExpandedWidth = 280;

    private readonly InventoryStore _store;
    private readonly BackupManager _backupManager;
    private readonly AppLogWriter _appLog;
    private readonly string _adDomainFqdn;
    private IAdService _adService;
    private string? _adOverrideUsername;
    private readonly string _suUsername;
    private readonly ISecurityAudit _audit;
    /// <summary>Separate, unbounded, tamper-evident ledger of every inventory field change - "for
    /// when I get audited I know what happened to each user and who did what" (Francois,
    /// 2026-09-15). Deliberately its own log, not mixed into _audit's security-events ledger, since
    /// this is routine business activity, not a security signal.</summary>
    private readonly ISecurityAudit _inventoryAudit;
    private readonly Dictionary<string, DataGridView> _grids = new();
    private readonly Dictionary<string, TextBox> _searchBoxes = new();
    private readonly Dictionary<string, Label> _statusLabels = new();
    private readonly Dictionary<string, string> _baseFilters = new();
    private readonly Dictionary<string, bool> _contextMenuValidRow = new();
    private readonly HashSet<string> _expandedGroups = new(StringComparer.OrdinalIgnoreCase);
    private readonly ListBox _logListBox = new() { Dock = DockStyle.Fill, Font = new Font("Consolas", 8), HorizontalScrollbar = true, IntegralHeight = false };
    private bool _logCollapsed;
    private DateTime _lastKnownFileWriteTimeUtc = DateTime.MinValue;
    private TabControl _tabs = null!;
    private DateTime _appLogTailDate = DateTime.MinValue;
    private long _appLogTailLength;

    /// <summary>"Users with no Devices" - Francois, 2026-09-25: "I want to scan AD and import all
    /// of the users who are in AD and are currently not being matched to a device." Direct port of
    /// the PowerShell build's own tab of the same name - a genuinely different KIND of tab from
    /// every other one in this app: it isn't a filtered view over _store.Items at all (not even a
    /// virtual one like Off Domain), it's a live, on-demand AD report populated only by "Refresh
    /// from AD" - so it deliberately stays out of _grids/_baseFilters and every generic per-tab
    /// refresh/filter/context-menu machinery those drive.</summary>
    private readonly DataGridView _gridNoDeviceUsers = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        MultiSelect = true,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
    };
    private readonly Label _lblNoDeviceStatus = new()
    {
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = Color.DarkRed,
        Text = "Click 'Refresh from AD' to load enabled AD users with no device on record.",
    };
    private IReadOnlyList<AdUserNoDevice> _lastNoDeviceResults = [];

    /// <summary>Terminations scheduled for later (shared file next to inventory.json). Nothing runs
    /// by itself: <see cref="CheckDueTerminations"/> prompts whichever admin has the app open.</summary>
    private readonly ScheduledTerminationStore _scheduledTerminations;
    private static readonly TimeSpan TerminationPromptRepeat = TimeSpan.FromMinutes(30);
    private DateTime _nextTerminationPromptUtc = DateTime.MinValue;
    private bool _terminationPromptOpen;

    /// <summary>Cached for <see cref="ProtectedAccountCacheLifetime"/> - see
    /// GetProtectedAccountPolicy. Null until first needed.</summary>
    private ProtectedAccountPolicy? _protectedAccounts;
    private DateTime _protectedAccountsLoadedUtc = DateTime.MinValue;
    private static readonly TimeSpan ProtectedAccountCacheLifetime = TimeSpan.FromMinutes(10);

    public MainForm(string dataFilePath, IAdService adService, string suUsername, string adDomainFqdn, ISecurityAudit? audit = null, ISecurityAudit? inventoryAudit = null)
    {
        _store = new InventoryStore(dataFilePath);
        _scheduledTerminations = new ScheduledTerminationStore(Path.Combine(Path.GetDirectoryName(dataFilePath) ?? ".", "scheduled-terminations.json"));
        _backupManager =new BackupManager(Path.Combine(Path.GetDirectoryName(dataFilePath) ?? ".", "Backups"), MaxBackups);
        _appLog = new AppLogWriter(Path.Combine(Path.GetDirectoryName(dataFilePath) ?? ".", "Logs"));
        // Baseline for TailSharedActivityLog, captured BEFORE this session writes its own first
        // line below - so that line's own bytes get folded into the baseline (via Log()'s own
        // resync) rather than replayed back to us a moment later by the tailer. Deliberately does
        // NOT backfill anything already in today's shared file from other sessions before this one
        // launched - see TailSharedActivityLog's own comment for why "live going forward" was
        // chosen over "replay today's full history."
        InitializeAppLogTailBaseline();
        _adService = adService;
        _adDomainFqdn = adDomainFqdn;
        _suUsername = suUsername;
        _audit = audit ?? NullSecurityAudit.Instance;
        _inventoryAudit = inventoryAudit ?? NullSecurityAudit.Instance;

        // Mop up any SU-credential handoff file a prior run orphaned (crash, AllSigned block, or a
        // window closed before the script's own cleanup) - see TryExportSuCredentialFile.
        SweepStaleOffboardCredentialFiles();

        Icon = AppIcon.Shared;
        Text = "Inventory Manager";
        Size = new Size(1450, 650);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(900, 400);
        WindowState = FormWindowState.Maximized;

        _tabs = new TabControl { Dock = DockStyle.Fill };
        Controls.Add(_tabs);

        // Every real status tab excludes Off-Domain devices (they surface in the dedicated Off
        // Domain tab below instead) and offers "Off Domain" as an extra Move-to option alongside
        // the other 6 real statuses - port of the PowerShell build's per-tab $DomainMoveTarget.
        foreach (var status in InventorySchema.Statuses)
        {
            var moveTargets = InventorySchema.Statuses
                .Where(s => s.Key != status.Key)
                .Select(s => new MoveTarget(s.Label, s.Key, null))
                .Append(new MoveTarget("Off Domain", null, "Off-Domain"))
                .ToList();
            _baseFilters[status.Key] = $"[Status] = '{status.Key}' AND [Domain] <> 'Off-Domain'";
            _tabs.TabPages.Add(BuildTab(status.Key, status.Label, moveTargets, hideCreateButtons: false));
        }

        // "Off Domain" is a filtered view keyed on the Domain field, not a real Status - a device
        // can be both "Current Inventory" and Off-Domain at once, so it isn't auto- or manually
        // moved into this view the way the 7 real statuses work; Add/Import are hidden since a
        // brand-new row here would need an invalid Status value ("OffDomain" isn't real).
        var offDomainMoveTargets = InventorySchema.Statuses
            .Select(s => new MoveTarget(s.Label, s.Key, null))
            .Append(new MoveTarget("On Domain (CONTOSO)", null, "CONTOSO"))
            .ToList();
        _baseFilters[OffDomainTabKey] = "[Domain] = 'Off-Domain'";
        _tabs.TabPages.Add(BuildTab(OffDomainTabKey, "Off Domain", offDomainMoveTargets, hideCreateButtons: true));

        _tabs.TabPages.Add(BuildUsersWithNoDeviceTab());

        // Docking order matters: Fill, then Right/Left, then Top/Bottom - an earlier-added Dock
        // claims its space first, so adding Right before Bottom/Top (matching the PS build's own
        // hard-learned control-add order) keeps the log panel from overlapping the bottom button
        // or the menu bar.
        Controls.Add(BuildLogPanel());

        // Always visible regardless of which tab is active, same as the PowerShell build -
        // onboarding doesn't act on whatever's selected in the current tab.
        var btnOnboard = new Button { Text = "Onboard New User", Dock = DockStyle.Bottom, Height = 30 };
        btnOnboard.Click += (_, _) => OpenOnboardingWizard();
        Controls.Add(btnOnboard);

        Controls.Add(BuildMenuStrip());

        LoadAndRefreshAll();
        Log($"Logged in as '{_suUsername}'.");

        var refreshTimer = new System.Windows.Forms.Timer { Interval = 15_000 };
        refreshTimer.Tick += (_, _) =>
        {
            RefreshFromDiskIfChanged();
            TailSharedActivityLog();
            CheckDueTerminations();
        };
        refreshTimer.Start();

        var backupError = _backupManager.RunPeriodicCheck(_store.DataFilePath);
        if (backupError is not null)
        {
            MessageBox.Show(this, $"Warning: automatic backup check failed: {backupError}", "Backup Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        var backupTimer = new System.Windows.Forms.Timer { Interval = (int)BackupInterval.TotalMilliseconds };
        backupTimer.Tick += (_, _) =>
        {
            var error = _backupManager.RunPeriodicCheck(_store.DataFilePath);
            if (error is not null)
            {
                MessageBox.Show(this, $"Warning: automatic backup check failed: {error}", "Backup Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
        backupTimer.Start();

        FormClosing += MainForm_FormClosing;

        // The "it's time" prompt for scheduled terminations: once at launch (after the window is
        // up, so the dialog has a visible parent), then again from the 15s refresh timer above.
        Shown += (_, _) => BeginInvoke(CheckDueTerminations);
    }

    /// <summary>Records the other half of the "who was using the tool and when" audit pair -
    /// LoginSuccess is already recorded during the login gate (LoginGate.RunSuLogin) before this
    /// form ever exists; this fires whenever the operator closes the main window, by whatever
    /// route (the X button, Alt+F4, or a real Windows logoff/shutdown while the app is still
    /// open). Deliberately unconditional - even a crash-adjacent close still gets a best-effort
    /// SessionEnded entry rather than silently leaving only a login with no matching logout.</summary>
    private void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        Log($"Logging out - '{_suUsername}' closing the session.");
        try
        {
            _audit.Record(SecurityEventType.SessionEnded, SecuritySeverity.Info, _suUsername, "", "Session ended - operator closed Inventory Manager.");
        }
        catch
        {
            // Best-effort only - never block the app from actually closing over a logging failure.
        }
    }

    private MenuStrip BuildMenuStrip()
    {
        var menuStrip = new MenuStrip { Dock = DockStyle.Top };
        var fileMenu = new ToolStripMenuItem("File");

        var menuRestore = new ToolStripMenuItem("Restore from Backup...");
        menuRestore.Click += (_, _) => ShowRestoreBackupDialog();
        fileMenu.DropDownItems.Add(menuRestore);

        var menuOpenBackups = new ToolStripMenuItem("Open Backups Folder");
        menuOpenBackups.Click += (_, _) => OpenBackupsFolder();
        fileMenu.DropDownItems.Add(menuOpenBackups);

        fileMenu.DropDownItems.Add(new ToolStripSeparator());

        var menuAdSync = new ToolStripMenuItem("Active Directory Sync...");
        menuAdSync.Click += (_, _) => ShowActiveDirectorySyncDialog();
        fileMenu.DropDownItems.Add(menuAdSync);

        var menuSearchAd = new ToolStripMenuItem("Search Active Directory...");
        menuSearchAd.Click += (_, _) => ShowAdUserSearchDialog();
        fileMenu.DropDownItems.Add(menuSearchAd);

        var menuDuplicates = new ToolStripMenuItem("Find Duplicate Devices...");
        menuDuplicates.Click += (_, _) => ShowDuplicateDevicesDialog();
        fileMenu.DropDownItems.Add(menuDuplicates);

        var menuImportCellPhones = new ToolStripMenuItem("Import Cell Phone Numbers...");
        menuImportCellPhones.Click += (_, _) => ShowCellPhoneImportDialog();
        fileMenu.DropDownItems.Add(menuImportCellPhones);

        fileMenu.DropDownItems.Add(new ToolStripSeparator());

        var menuOnboard = new ToolStripMenuItem("Onboard New User...") { ShortcutKeys = Keys.Control | Keys.N };
        menuOnboard.Click += (_, _) => OpenOnboardingWizard();
        fileMenu.DropDownItems.Add(menuOnboard);

        var menuCopyPermissions = new ToolStripMenuItem("Copy User Permissions...");
        menuCopyPermissions.Click += (_, _) => OpenCopyPermissions(null);
        fileMenu.DropDownItems.Add(menuCopyPermissions);

        var menuScheduledTerminations = new ToolStripMenuItem("Scheduled Terminations...");
        menuScheduledTerminations.Click += (_, _) => ShowScheduledTerminations(null);
        fileMenu.DropDownItems.Add(menuScheduledTerminations);

        var menuSetAdCred = new ToolStripMenuItem("Run AD Actions As...");
        menuSetAdCred.Click += (_, _) => ShowAdCredentialDialog();
        fileMenu.DropDownItems.Add(menuSetAdCred);

        var menuClearAdCred = new ToolStripMenuItem("Use My Windows Session for AD");
        menuClearAdCred.Click += (_, _) => ClearAdCredential();
        fileMenu.DropDownItems.Add(menuClearAdCred);

        fileMenu.DropDownOpening += (_, _) =>
        {
            menuSetAdCred.Text = _adOverrideUsername is not null ? $"Run AD Actions As... (currently: {_adOverrideUsername})" : "Run AD Actions As...";
            menuClearAdCred.Enabled = _adOverrideUsername is not null;
            menuScheduledTerminations.Text = ScheduledTerminationsMenuText();
        };

        menuStrip.Items.Add(fileMenu);

        var securityMenu = new ToolStripMenuItem("Security");
        var menuVerifyLog = new ToolStripMenuItem("Verify Security Log...");
        menuVerifyLog.Click += (_, _) => VerifyAuditLog(_audit, "Security Log");
        securityMenu.DropDownItems.Add(menuVerifyLog);
        var menuViewEvents = new ToolStripMenuItem("View Recent Security Events...");
        menuViewEvents.Click += (_, _) => ShowRecentAuditEvents(_audit, "Security Log");
        securityMenu.DropDownItems.Add(menuViewEvents);
        securityMenu.DropDownItems.Add(new ToolStripSeparator());
        var menuVerifyChangeLog = new ToolStripMenuItem("Verify Inventory Change Log...");
        menuVerifyChangeLog.Click += (_, _) => VerifyAuditLog(_inventoryAudit, "Inventory Change Log");
        securityMenu.DropDownItems.Add(menuVerifyChangeLog);
        var menuViewChanges = new ToolStripMenuItem("View Recent Inventory Changes...");
        menuViewChanges.Click += (_, _) => ShowRecentAuditEvents(_inventoryAudit, "Inventory Change Log");
        securityMenu.DropDownItems.Add(menuViewChanges);
        menuStrip.Items.Add(securityMenu);

        // A plain top-level item (no dropdown), not nested under File - Francois asked for it to
        // sit right next to Security as its own one-click button, not tucked away in a submenu.
        var menuDocumentation = new ToolStripMenuItem("Documentation");
        menuDocumentation.Click += (_, _) => OpenDocumentation();
        menuStrip.Items.Add(menuDocumentation);

        return menuStrip;
    }

    /// <summary>Opens the User Guide that ships alongside the app (see DocumentationPath /
    /// InventoryManager.App.csproj) in the operator's default browser - works offline, on any
    /// admin's machine, with no dependency on the separately published claude.ai artifact or a
    /// network share being reachable.</summary>
    private void OpenDocumentation()
    {
        if (!File.Exists(DocumentationPath))
        {
            MessageBox.Show(this, $"Documentation file not found at:\r\n{DocumentationPath}", "Documentation", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(DocumentationPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not open the documentation: {ex.Message}", "Documentation", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ShowActiveDirectorySyncDialog()
    {
        using var dialog = new ActiveDirectorySyncDialog(_adService, _store, SaveAndRefresh, Log);
        dialog.ShowDialog(this);
    }

    private void ShowAdUserSearchDialog()
    {
        using var dialog = new AdUserSearchDialog(_adService);
        dialog.ShowDialog(this);
    }

    private void ShowDuplicateDevicesDialog()
    {
        using var dialog = new DuplicateDevicesDialog(_store, SaveAndRefresh, SelectRowInGrid, msg => Log(msg));
        dialog.ShowDialog(this);
    }

    /// <summary>Francois, 2026-09-23: "I have a list of cell phones in a csv... match what cell
    /// phone belongs to what user and I can manually update the inventory." Picks the file here
    /// (same convention as ImportItemsCsv), parses and matches it against Current Inventory by
    /// name (see CellPhoneMatcher), then hands the result to CellPhoneImportDialog for review -
    /// nothing is written to any record until the operator explicitly applies rows there.</summary>
    private void ShowCellPhoneImportDialog()
    {
        using var ofd = new OpenFileDialog { Filter = "CSV Files (*.csv)|*.csv|All Files (*.*)|*.*" };
        if (ofd.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        List<CellPhoneCsvRow> csvRows;
        try
        {
            var content = File.ReadAllText(ofd.FileName);
            csvRows = CellPhoneCsvParser.Parse(content);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not read '{Path.GetFileName(ofd.FileName)}': {ex.Message}", "Import Cell Phone Numbers", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        if (csvRows.Count == 0)
        {
            MessageBox.Show(this, "No usable rows found in that file.", "Import Cell Phone Numbers", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var currentItems = _store.Items.Where(i => i.Status == "Current").ToList();
        var matchResult = CellPhoneMatcher.Match(csvRows, currentItems);
        Log($"Opened cell phone import from '{Path.GetFileName(ofd.FileName)}': {csvRows.Count} row(s) - {matchResult.Matched.Count} matched, {matchResult.Ambiguous.Count} ambiguous, {matchResult.Unmatched.Count} unmatched.");

        using var dialog = new CellPhoneImportDialog(matchResult, SaveAndRefresh, msg => Log(msg));
        dialog.ShowDialog(this);
    }

    /// <summary>Port of the PowerShell build's own "Users with no Devices" tab
    /// (Get-AdUsersWithNoDevice) - a live, on-demand AD report, not a filtered view over
    /// _store.Items. Deliberately NOT built via BuildTab: it has no Move-to submenu, no right-click
    /// menu, no Add/Import CSV, and its grid is populated only by clicking "Refresh from AD," never
    /// by the generic per-tab refresh machinery every real status tab goes through.</summary>
    private TabPage BuildUsersWithNoDeviceTab()
    {
        var tab = new TabPage("Users with no Devices");
        var topPanel = new Panel { Dock = DockStyle.Top, Height = 40 };

        // FlowLayoutPanel for the buttons, not fixed Location - a 2026-09-23 bug in
        // CellPhoneImportDialog (fixed-Location buttons rendering off-screen once the window got
        // narrow) is exactly the class of mistake this avoids, here on a window (MainForm) that's
        // just as resizable.
        var buttonFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Padding = new Padding(0, 6, 8, 0),
        };
        var btnRefresh = new Button { Text = "Refresh from AD", Size = new Size(120, 26), Margin = new Padding(0, 0, 6, 0) };
        btnRefresh.Click += (_, _) => RefreshUsersWithNoDevice();
        buttonFlow.Controls.Add(btnRefresh);
        var btnImport = new Button { Text = "Import Selected", Size = new Size(120, 26) };
        btnImport.Click += (_, _) => ImportSelectedNoDeviceUsers();
        buttonFlow.Controls.Add(btnImport);

        topPanel.Controls.Add(_lblNoDeviceStatus);
        topPanel.Controls.Add(buttonFlow);

        tab.Controls.Add(_gridNoDeviceUsers);
        tab.Controls.Add(topPanel);
        return tab;
    }

    private void RefreshUsersWithNoDevice()
    {
        _gridNoDeviceUsers.Cursor = Cursors.WaitCursor;
        _lblNoDeviceStatus.Text = "Querying Active Directory...";
        Application.DoEvents();
        try
        {
            var allAdUsers = _adService.ListAllUsers();
            // Admin accounts never appear here, so they can't be imported - see
            // ProtectedAccountPolicy for the 9/30 incident that came from importing them.
            var protectedAccounts = GetProtectedAccountPolicy();
            var found = UsersWithNoDeviceFinder.Find(_store.Items, allAdUsers);
            _lastNoDeviceResults = found.Where(u => !protectedAccounts.IsProtected(u.Username)).ToList();
            var hidden = found.Count - _lastNoDeviceResults.Count;
            _gridNoDeviceUsers.DataSource = _lastNoDeviceResults
                .Select(u => new { u.Username, u.LastName, u.FirstName })
                .ToList();
            _lblNoDeviceStatus.Text = $"{_lastNoDeviceResults.Count} enabled AD user(s) with no device on record (as of {DateTime.Now:HH:mm:ss})." +
                (hidden > 0 ? $" {hidden} protected admin account(s) hidden." : "");
            Log($"Users with no Devices: found {_lastNoDeviceResults.Count} enabled AD user(s) with no matching inventory record ({hidden} protected admin account(s) excluded).");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not query Active Directory: {ex.Message}", "Active Directory Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _lblNoDeviceStatus.Text = "Could not query Active Directory - see error dialog.";
        }
        finally
        {
            _gridNoDeviceUsers.Cursor = Cursors.Default;
        }
    }

    /// <summary>Beyond the PowerShell build's original scope (which was read-only) - Francois,
    /// 2026-09-25: "import all of the users who are in AD and are currently not being matched to a
    /// device." Creates one new Current Inventory row per selected AD user, using the same "No PC"
    /// synthetic-device placeholder InventoryCsvImporter already established for an assigned-user-
    /// only row with no real device. Re-runs the AD scan afterward so imported users disappear from
    /// this tab on their own, the same self-correcting way every other AD-classification flow in
    /// this app already works - no separate dedupe bookkeeping needed.</summary>
    private void ImportSelectedNoDeviceUsers()
    {
        if (_gridNoDeviceUsers.SelectedRows.Count == 0)
        {
            MessageBox.Show(this, "Select one or more users to import.", "No Selection", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var selectedUsernames = _gridNoDeviceUsers.SelectedRows.Cast<DataGridViewRow>()
            .Select(r => (string)((dynamic)r.DataBoundItem!).Username)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var protectedAccounts = GetProtectedAccountPolicy();
        var toImport = _lastNoDeviceResults
            .Where(u => selectedUsernames.Contains(u.Username) && !protectedAccounts.IsProtected(u.Username))
            .ToList();
        if (toImport.Count == 0)
        {
            return;
        }

        var added = new List<string>();
        foreach (var user in toImport)
        {
            var item = new InventoryItem
            {
                Id = InventorySchema.GetNextId(_store.Items),
                Status = "Current",
                DeviceName = InventorySchema.NoDevicePlaceholder,
                Username = user.Username,
                FirstName = user.FirstName,
                LastName = user.LastName,
            };
            _store.Items.Add(item);
            added.Add($"{user.FirstName} {user.LastName} ({user.Username})".Trim());
        }
        SaveAndRefresh();
        Log($"Imported {added.Count} AD user(s) with no device into Current Inventory: {string.Join(", ", added)}.");
        MessageBox.Show(this, $"Imported {added.Count} user(s) into Current Inventory as \"No PC\" placeholder records.", "Import Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
        RefreshUsersWithNoDevice();
    }

    /// <summary>Port of Show-RowInMainGrid: switches to the row's tab, clears any active search
    /// filter, and selects/scrolls to the row - used by Find Duplicate Devices' "Go to Selected"
    /// while that dialog is still open (a modal dialog doesn't stop the window behind it from
    /// repainting).</summary>
    private void SelectRowInGrid(int id, string statusKey)
    {
        var tabIndex = InventorySchema.Statuses.ToList().FindIndex(s => s.Key == statusKey);
        if (tabIndex >= 0)
        {
            _tabs.SelectedIndex = tabIndex;
        }
        _searchBoxes[statusKey].Text = "";
        ApplyFilter(statusKey);
        var grid = _grids[statusKey];
        foreach (DataGridViewRow row in grid.Rows)
        {
            if (row.DataBoundItem is DataRowView view && (int)view.Row["Id"] == id)
            {
                grid.ClearSelection();
                row.Selected = true;
                var firstVisible = grid.Columns.Cast<DataGridViewColumn>().FirstOrDefault(c => c.Visible);
                if (firstVisible is not null)
                {
                    grid.CurrentCell = row.Cells[firstVisible.Index];
                }
                grid.FirstDisplayedScrollingRowIndex = Math.Max(0, row.Index - 3);
                break;
            }
        }
    }

    private void ShowAdCredentialDialog()
    {
        using var dialog = new AdCredentialDialog(_adDomainFqdn, _adOverrideUsername);
        if (dialog.ShowDialog(this) == DialogResult.OK && dialog.Result is { } credentials)
        {
            _adService = new AdService(_adDomainFqdn, credentials);
            _adOverrideUsername = credentials.Username;
        }
    }

    private void ClearAdCredential()
    {
        if (_adOverrideUsername is null)
        {
            MessageBox.Show(this, "No alternate AD credentials are set - already using your current Windows session.", "AD Credentials", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        _adService = new AdService(_adDomainFqdn);
        _adOverrideUsername = null;
        MessageBox.Show(this, "Cleared. Active Directory actions will use your current Windows session again.", "AD Credentials Cleared", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void ShowRestoreBackupDialog()
    {
        if (_backupManager.ListBackups().Count == 0)
        {
            MessageBox.Show(this, "No backups exist yet - the first automatic backup is taken within 5 minutes of opening the app or making a change.", "Restore from Backup", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var dialog = new RestoreBackupDialog(_backupManager, _store, MaxBackups);
        if (dialog.ShowDialog(this) == DialogResult.OK && dialog.Restored)
        {
            RefreshAllTabs();
        }
    }

    private void OpenBackupsFolder()
    {
        var backupDir = Path.Combine(Path.GetDirectoryName(_store.DataFilePath) ?? ".", "Backups");
        try
        {
            Directory.CreateDirectory(backupDir);
            Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"\"{backupDir}\"", UseShellExecute = false });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not open the backups folder: {ex.Message}", "Open Backups Folder", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>Shared by both the security-events ledger and the inventory-change ledger - same
    /// hash-chain verification, just pointed at whichever ISecurityAudit instance (and display
    /// name) the caller passes in.</summary>
    private void VerifyAuditLog(ISecurityAudit audit, string logName)
    {
        if (audit is not ISecurityLogReader reader)
        {
            MessageBox.Show(this, $"No {logName.ToLowerInvariant()} is configured for this session.", logName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var result = reader.Verify();
        var count = reader.ReadAll().Count;
        if (result.IsIntact)
        {
            MessageBox.Show(this,
                $"{logName} integrity: OK.{Environment.NewLine}{Environment.NewLine}{count} event(s) recorded; the tamper-evident chain is intact from the genesis entry.",
                $"{logName} Verified", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        else
        {
            MessageBox.Show(this,
                $"WARNING - the {logName.ToLowerInvariant()} FAILED its integrity check.{Environment.NewLine}{Environment.NewLine}{result.Message}{Environment.NewLine}{Environment.NewLine}The ledger was altered or truncated after it was written. Preserve the file and investigate - someone may be covering their tracks.",
                $"{logName} TAMPERED", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ShowRecentAuditEvents(ISecurityAudit audit, string logName)
    {
        if (audit is not ISecurityLogReader reader)
        {
            MessageBox.Show(this, $"No {logName.ToLowerInvariant()} is configured for this session.", logName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var entries = reader.ReadAll();
        if (entries.Count == 0)
        {
            MessageBox.Show(this, $"No {logName.ToLowerInvariant()} events have been recorded yet.", logName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var integrity = reader.Verify();
        var header = integrity.IsIntact
            ? $"Chain intact - {entries.Count} event(s). Newest first:"
            : $"*** INTEGRITY CHECK FAILED: {integrity.Message} *** - showing what is on disk, newest first:";
        ShowAuditEventsDialog($"Recent {logName} Events", header, entries.Reverse().Take(200));
    }

    /// <summary>The per-record equivalent of ShowRecentAuditEvents (Security menu): pulls every
    /// entry the Inventory Change Log has ever recorded for THIS device - unbounded, unlike AD
    /// Notes' ~1000-char cap - by filtering on SecurityLogEntry.Event.Target, which
    /// RecordFieldChangeAudits deliberately keys on DeviceName (not the linked Username, which
    /// drifts across reassignments) specifically so this filter stays correct across the device's
    /// whole history. Right-click, single device only - "pull the log for this record."</summary>
    private void ShowDeviceChangeHistory(string statusKey)
    {
        var item = GetSelectedItem(statusKey);
        if (item is null)
        {
            MessageBox.Show(this, "Select exactly one device to view its change history.", "No Selection", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (_inventoryAudit is not ISecurityLogReader reader)
        {
            MessageBox.Show(this, "No Inventory Change Log is configured for this session.", "Change History", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var deviceName = item.DeviceName.Trim();
        Log($"Viewed change history for '{deviceName}'.");
        var entries = reader.ReadAll()
            .Where(e => string.Equals(e.Event.Target, deviceName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (entries.Count == 0)
        {
            MessageBox.Show(this, $"No changes have been recorded yet for '{deviceName}'.", "Change History", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var integrity = reader.Verify();
        var header = integrity.IsIntact
            ? $"{entries.Count} recorded change(s) for '{deviceName}'. Newest first:"
            : $"*** INTEGRITY CHECK FAILED on the underlying Inventory Change Log: {integrity.Message} *** - showing what is on disk, newest first:";
        ShowAuditEventsDialog($"Change History - {deviceName}", header, entries.AsEnumerable().Reverse());
    }

    private void ShowAuditEventsDialog(string title, string header, IEnumerable<SecurityLogEntry> entriesToShow)
    {
        var lines = entriesToShow.Select(e => $"[{e.Event.TimestampUtc}] {e.Event.Severity,-8} {e.Event.Type} | actor={e.Event.Actor} target={e.Event.Target} | {e.Event.Detail}");

        using var dlg = new Form
        {
            Icon = AppIcon.Shared,
            Text = title,
            Size = new Size(900, 500),
            StartPosition = FormStartPosition.CenterParent,
        };
        var box = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Dock = DockStyle.Fill,
            Font = new Font("Consolas", 8),
            Text = header + Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, lines),
        };
        dlg.Controls.Add(box);
        dlg.ShowDialog(this);
    }

    /// <summary>Copy User Permissions (File menu, or a row's context menu with that row's user
    /// pre-filled as the source). Records land in Logs\PermissionCopies beside the app log.</summary>
    private void OpenCopyPermissions(AdUserInfo? initialSource)
    {
        Log("Opened Copy User Permissions.");
        using var dialog = new CopyPermissionsDialog(_adService, _suUsername, _audit, Log,
            Path.Combine(_appLog.LogDirectory, "PermissionCopies"), initialSource,
            isProtectedTarget: sam => RefuseIfProtectedAccount(sam, "Copy User Permissions"));
        dialog.ShowDialog(this);
    }

    private void InvokeCopyPermissionsFromRow(string statusKey)
    {
        var username = GetSelectedItem(statusKey)?.Username.Trim();
        if (string.IsNullOrWhiteSpace(username))
        {
            MessageBox.Show(this, "This device has no linked AD username to copy permissions from.", "Copy User Permissions", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        AdUserInfo? adUser;
        try
        {
            adUser = _adService.GetUser(username);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not look up '{username}' in Active Directory: {ex.Message}", "Active Directory Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        if (adUser is null)
        {
            MessageBox.Show(this, $"Could not find '{username}' in Active Directory.", "Active Directory Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        OpenCopyPermissions(adUser);
    }

    private void OpenOnboardingWizard()
    {
        Log("Opened the Onboarding wizard.");
        using var wizard = new OnboardingWizardForm(_adService, _store, OnboardingUpnSuffix, _suUsername, Log, _audit);
        if (wizard.ShowDialog(this) == DialogResult.OK)
        {
            RefreshAllTabs();
        }
        else
        {
            Log("Onboarding wizard closed without completing (cancelled).");
        }
    }

    /// <summary>
    /// Port of the PowerShell build's Invoke-TransferUser: the single consolidated Transfer
    /// action (Manager/OU/Computer/Description/Office, each independently changeable, required
    /// ticket). A device with First/Last but no linked AD username is still handled - just
    /// restricted to a Computer-only change, matching the old computer-only Transfer's capability
    /// so it isn't lost now that there's one action instead of two.
    /// </summary>
    private void InvokeTransferUser(string statusKey)
    {
        var selected = GetSelectedItem(statusKey);
        if (selected is null)
        {
            MessageBox.Show(this, "Select exactly one device to transfer its user.", "Transfer User", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (string.IsNullOrWhiteSpace(selected.FirstName.Trim()) && string.IsNullOrWhiteSpace(selected.LastName.Trim()) && string.IsNullOrWhiteSpace(selected.Username.Trim()))
        {
            MessageBox.Show(this, $"'{selected.DeviceName}' has no assigned user to transfer.", "Transfer User", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (!string.IsNullOrWhiteSpace(selected.Username) && RefuseIfProtectedAccount(selected.Username.Trim(), "Transfer User"))
        {
            return;
        }

        // Locked for the rest of this method, released in the finally below regardless of how we
        // exit - cancel, an AD error, or a completed transfer all need to give the record back.
        var row = TryAcquireEditLock(selected.Id, "transferred");
        if (row is null)
        {
            return;
        }
        Log($"Opened '{row.DeviceName}' for transfer.");
        try
        {
            var username = row.Username.Trim();
            var firstName = row.FirstName.Trim();
            var lastName = row.LastName.Trim();
            var fullName = $"{firstName} {lastName}".Trim();

            var hasAdUser = !string.IsNullOrWhiteSpace(username);
            var currentManagerDisplay = "(none)";
            var currentOuDn = "";
            var currentDescription = "";
            var currentOffice = "";
            AdUserInfo? adUser = null;
            if (hasAdUser)
            {
                try
                {
                    adUser = _adService.GetUser(username);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, $"Could not look up '{username}' in Active Directory: {ex.Message}", "Active Directory Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                if (adUser is null)
                {
                    MessageBox.Show(this, $"Could not find '{username}' in Active Directory.", "Active Directory Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                if (!string.IsNullOrWhiteSpace(adUser.ManagerDistinguishedName))
                {
                    try
                    {
                        var manager = _adService.GetUserByDistinguishedName(adUser.ManagerDistinguishedName);
                        currentManagerDisplay = manager is null ? adUser.ManagerDistinguishedName : $"{manager.GivenName} {manager.Surname} ({manager.SamAccountName})".Trim();
                    }
                    catch
                    {
                        currentManagerDisplay = adUser.ManagerDistinguishedName;
                    }
                }
                currentOuDn = DistinguishedNameHelper.GetParentOuDn(adUser.DistinguishedName);
                currentDescription = adUser.Description;
                currentOffice = adUser.Office;
            }

            using var dialog = new TransferUserDialog(_adService, _store.Items, row, fullName, currentManagerDisplay, currentOuDn, currentDescription, currentOffice, hasAdUser);
            if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Result is not { } result)
            {
                Log($"Closed '{row.DeviceName}' - transfer cancelled, no changes made.");
                return;
            }

            var changes = new List<string>();
            if (result.ManagerChanged)
            {
                changes.Add($"Manager: {currentManagerDisplay} -> {result.NewManagerDisplay}");
            }
            if (result.OuChanged)
            {
                changes.Add($"OU: {currentOuDn} -> {result.NewOuDn}");
            }
            if (result.ComputerChanged && result.NewComputerItem is not null)
            {
                changes.Add($"Computer: {row.DeviceName} -> {result.NewComputerItem.DeviceName}");
            }
            if (result.DescriptionChanged)
            {
                changes.Add($"Description: '{currentDescription}' -> '{result.NewDescription}'");
            }
            if (result.OfficeChanged)
            {
                changes.Add($"Office: '{currentOffice}' -> '{result.NewOffice}'");
            }

            var confirm = MessageBox.Show(this,
                $"Transfer {fullName} (ticket # {result.TicketNumber})?\r\n\r\n{string.Join("\r\n", changes)}",
                "Confirm Transfer", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (confirm != DialogResult.Yes)
            {
                Log($"Closed '{row.DeviceName}' - transfer not confirmed, no changes made.");
                return;
            }

            var anyFailure = false;
            if (hasAdUser)
            {
                var request = new TransferRequest(
                    SamAccountName: username,
                    HasAdUser: true,
                    CurrentUserDistinguishedName: adUser!.DistinguishedName,
                    TicketNumber: result.TicketNumber,
                    ManagerChanged: result.ManagerChanged, NewManagerDn: result.NewManagerDn, NewManagerDisplay: result.NewManagerDisplay, CurrentManagerDisplay: currentManagerDisplay,
                    OuChanged: result.OuChanged, NewOuDn: result.NewOuDn, CurrentOuDn: currentOuDn,
                    DescriptionChanged: result.DescriptionChanged, NewDescription: result.NewDescription, CurrentDescription: currentDescription,
                    OfficeChanged: result.OfficeChanged, NewOffice: result.NewOffice, CurrentOffice: currentOffice,
                    ComputerChanged: result.ComputerChanged, CurrentDeviceName: row.DeviceName, NewDeviceName: result.NewComputerItem?.DeviceName ?? "",
                    SuUsername: _suUsername);

                var transferService = new TransferService(_adService, audit: _audit);
                var transferResult = transferService.ApplyChanges(request);
                anyFailure = transferResult.AnyFailure;
                foreach (var outcome in transferResult.Outcomes)
                {
                    if (!outcome.Success)
                    {
                        Log($"Transfer User: could NOT change '{username}''s {outcome.FieldName}: {outcome.Detail}", AppLogLevel.Error);
                        MessageBox.Show(this, $"Transfer User: could NOT change {outcome.FieldName}: {outcome.Detail}", "Transfer User", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        continue;
                    }
                    var detail = outcome.FieldName switch
                    {
                        "Manager" => $"manager changed to '{result.NewManagerDisplay}'",
                        "OU" => $"moved to OU '{result.NewOuDn}'",
                        "Description" => $"Description changed to '{result.NewDescription}'",
                        "Office" => $"Office changed to '{result.NewOffice}'",
                        _ => $"{outcome.FieldName} changed",
                    };
                    Log($"Transfer User: '{username}' {detail}.");
                }
            }

            if (result.ComputerChanged && result.NewComputerItem is not null)
            {
                ComputerIdentityHandoff.Apply(_adService, row, result.NewComputerItem, firstName, lastName, username, row.ADFlagReason, DisabledComputersOuDn);
            }

            Log($"Transfer User complete for '{fullName}' (ticket # {result.TicketNumber}). Changed: {string.Join("; ", changes)}");
            var failureNote = anyFailure ? "\r\n\r\nSome AD changes failed - see the warning(s) shown." : "";
            MessageBox.Show(this, $"Transfer complete for {fullName}.\r\n\r\n{string.Join("\r\n", changes)}{failureNote}", "Transfer Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        finally
        {
            // One save covers both releasing the lock and persisting whatever changed above -
            // SaveAndRefresh's own diff-merge only writes what actually changed either way.
            RecordEditLock.Release(row, _suUsername);
            SaveAndRefresh();
        }
    }

    /// <summary>
    /// Francois decided 2026-09-15 that offboarding should just launch the real, existing
    /// Offboarding2026.ps1 tool directly (it already covers every AD/Mailbox/Cloud step) rather
    /// than a native re-implementation of a subset of it - this replaces the earlier native 7-step
    /// dialog entirely. Inventory Manager's own remaining job is just keeping ITS OWN data (the
    /// inventory row's Status, and the linked COMPUTER object - never touched by the script itself,
    /// which is scoped to the user account) in sync once the operator finishes in the real tool.
    /// </summary>
    private void InvokeOffboardUser(string statusKey)
    {
        var selected = GetSelectedItem(statusKey);
        if (selected is null)
        {
            MessageBox.Show(this, "Select exactly one device to offboard its user.", "Offboard User", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        StartOffboarding(selected);
    }

    /// <summary>The Offboard User flow for one device row - shared by the right-click command and
    /// by a due scheduled termination. Returns true only when the offboarding tool actually
    /// launched; every refusal/failure tells the operator why and returns false.</summary>
    private bool StartOffboarding(InventoryItem selected)
    {
        var username = selected.Username.Trim();
        if (string.IsNullOrWhiteSpace(username))
        {
            Log($"Offboard attempted on '{selected.DeviceName}' but it has no linked AD username - refused.", AppLogLevel.Warning);
            MessageBox.Show(this, $"'{selected.DeviceName}' has no linked AD username. Use 'Match User' to link this device to a real AD account first.", "Offboard User", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }

        // Honeytoken: any attempt to offboard a monitored decoy account is a strong tamper signal.
        // The native OffboardingService used to perform this exact check before it ran anything -
        // preserved here since launching the external script bypasses that service entirely, and
        // the script itself has no knowledge of this app's honeytoken policy.
        if (HoneytokenPolicy.IsHoneytokenUser(username))
        {
            _audit.Record(SecurityEventType.HoneytokenTripped, SecuritySeverity.Critical, _suUsername, username,
                $"Offboarding attempted against honeytoken account '{username}'. Refused; nothing changed.");
            Log($"Offboard attempted against honeytoken account '{username}' - refused and alerted.", AppLogLevel.Error);
            MessageBox.Show(this, $"'{username}' is a monitored decoy account. This attempt has been logged and alerted. Nothing was changed.", "Offboard User", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        if (RefuseIfProtectedAccount(username, "Offboard User"))
        {
            return false;
        }

        var itemId = selected.Id;
        var row = TryAcquireEditLock(itemId, "offboarded");
        if (row is null)
        {
            return false;
        }
        var deviceName = row.DeviceName.Trim();

        if (!File.Exists(OffboardingScriptPath))
        {
            MessageBox.Show(this, $"Offboarding script not found at:\r\n{OffboardingScriptPath}", "Offboard User", MessageBoxButtons.OK, MessageBoxIcon.Error);
            RecordEditLock.Release(row, _suUsername);
            SaveAndRefresh();
            return false;
        }

        // The tool reports what actually happened - every Run, per-step results, and an undo
        // journal of what each real run changed - to this per-launch file in the operator's own
        // (user-private) %TEMP%. Nothing on the device/computer side is offered unless that record
        // shows a real run for THIS user; see OffboardingOutcomeEvaluator. Added 2026-09-30 -
        // before that, closing the window always led to the "disable the computer?" prompt.
        var sessionPath = Path.Combine(Path.GetTempPath(), $"{OffboardingOutcomeEvaluator.SessionFilePrefix}{Guid.NewGuid():N}.json");
        var launched = LaunchOffboardingScript(
            ["-PrefillSearch", username, "-ResultFile", sessionPath],
            () => OnOffboardingToolClosed(itemId, deviceName, username, sessionPath));
        if (!launched)
        {
            RecordEditLock.Release(row, _suUsername);
            SaveAndRefresh();
            return false;
        }
        Log($"Launched the offboarding tool for '{username}' (device '{deviceName}')...", AppLogLevel.Info);
        return true;
    }

    /// <summary>Starts Offboarding2026.ps1 with <paramref name="scriptArgs"/> plus the SU
    /// credential handoff, and runs <paramref name="onClosed"/> on the UI thread once the tool's
    /// window closes. Shared by the normal offboarding launch and its undo mode. Returns false
    /// (after telling the operator) if the process couldn't be started.</summary>
    private bool LaunchOffboardingScript(IReadOnlyList<string> scriptArgs, Action onClosed)
    {
        // The signature is checked HERE, before anything secret exists: a script that fails never
        // gets the SU credential file. The file is then held open (shared-read, no writers) until
        // the tool exits, so it can't be swapped between this check and PowerShell reading it.
        FileStream scriptLock;
        try
        {
            scriptLock = new FileStream(OffboardingScriptPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not open the offboarding script: {ex.Message}", "Offboard User", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        var signature = ScriptSignatureVerifier.Verify(OffboardingScriptPath, OffboardingScriptSigning.AllowedSignerThumbprints);
        if (!signature.IsValid)
        {
            scriptLock.Dispose();
            _audit.Record(SecurityEventType.ScriptSignatureRefused, SecuritySeverity.Critical, _suUsername, Path.GetFileName(OffboardingScriptPath),
                $"Offboarding script refused ({signature.Status}): {signature.Detail}");
            Log($"The offboarding script failed its signature check ({signature.Status}) - not launched, no credentials were handed over.", AppLogLevel.Error);
            MessageBox.Show(this,
                $"The offboarding script failed its signature check, so it was not launched and no credentials were used.\r\n\r\n{signature.Detail}\r\n\r\nReinstall or update Inventory Manager. If this keeps happening, tell IT - the script may have been tampered with.",
                "Offboard User", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }

        // Hand the currently-authenticated SU identity to the script so its own on-prem AD writes
        // (Disable-ADAccount, Set-ADAccountPassword, etc.) run as that account instead of silently
        // falling back to whatever Windows session launched Inventory Manager - see
        // TryExportSuCredentialFile's own comment for why this matters and how it's kept safe.
        var credentialFilePath = TryExportSuCredentialFile();

        Process process;
        try
        {
            // ArgumentList (not a hand-quoted Arguments string) so .NET escapes each value
            // per-argument regardless of what it contains. Bypass, because the signature was just
            // verified above (valid AND pinned to the Contoso IT Scripts cert). AllSigned used to do
            // this job, but it applies to every module the script loads too: each Microsoft Graph/
            // Exchange module import made PowerShell prompt "run software from this untrusted
            // publisher?" in a console hidden behind the tool, which froze it (found 2026-10-08).
            var startInfo = new ProcessStartInfo { FileName = "powershell.exe", UseShellExecute = false };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(OffboardingScriptPath);
            foreach (var arg in scriptArgs)
            {
                startInfo.ArgumentList.Add(arg);
            }
            if (credentialFilePath is not null)
            {
                startInfo.ArgumentList.Add("-CredentialFile");
                startInfo.ArgumentList.Add(credentialFilePath);
            }
            process = Process.Start(startInfo)!;
        }
        catch (Exception ex)
        {
            scriptLock.Dispose();
            if (credentialFilePath is not null)
            {
                try { File.Delete(credentialFilePath); } catch { /* best-effort cleanup */ }
            }
            MessageBox.Show(this, $"Could not launch the offboarding tool: {ex.Message}", "Offboard User", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }

        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            scriptLock.Dispose();
            // The app OWNS cleanup of the exported SU credential file - never rely on the external
            // script having deleted it. If the script was AllSigned-blocked (bad/missing
            // signature), errored before its own cleanup line, or the window was closed early, the
            // DPAPI-encrypted SU credential would otherwise linger in %TEMP%, recoverable by any
            // process running as this same Windows user. By the time the child process has exited
            // it has long since read the file, so deleting here can't race the handoff.
            if (credentialFilePath is not null)
            {
                try { File.Delete(credentialFilePath); } catch { /* already gone if the script deleted it first */ }
            }
            // Exited fires on a background thread pool thread, and can fire long after this
            // method returns (the operator may spend many minutes in the external tool) - marshal
            // back to the UI thread, and don't touch the form at all if it's already gone.
            if (!IsDisposed)
            {
                BeginInvoke(onClosed);
            }
        };
        return true;
    }

    /// <summary>Deletes any stale offboarding SU-credential handoff file left in %TEMP% by a prior
    /// run that didn't get to clean up (crash, AllSigned block before the script's own delete, hard
    /// power-off). Runs once at startup; best-effort and never throws. Only files older than an
    /// hour are removed, so a concurrent same-user instance mid-offboard keeps its in-flight file.</summary>
    private static void SweepStaleOffboardCredentialFiles()
    {
        try
        {
            var dir = Path.GetTempPath();
            var candidates = Directory.EnumerateFiles(dir, TempCredentialCleanup.OffboardCredentialSearchPattern)
                .Select(path => (Path: path, LastWriteUtc: File.GetLastWriteTimeUtc(path)));
            foreach (var stale in TempCredentialCleanup.StaleFiles(candidates, DateTime.UtcNow, TimeSpan.FromHours(1)))
            {
                try { File.Delete(stale); } catch { /* best-effort - another instance may have it */ }
            }

            // Offboarding session records / undo reports (no secrets, but they hold the user's
            // pre-offboarding groups and settings). Normally deleted once the operator finishes,
            // undoes or defers; a crash can orphan one. A day's grace so a second instance's
            // long-open offboarding tool never has its live record swept.
            var sessionFiles = Directory.EnumerateFiles(dir, OffboardingOutcomeEvaluator.SessionFileSearchPattern)
                .Concat(Directory.EnumerateFiles(dir, OffboardingOutcomeEvaluator.UndoReportFileSearchPattern))
                .Select(path => (Path: path, LastWriteUtc: File.GetLastWriteTimeUtc(path)));
            foreach (var stale in TempCredentialCleanup.StaleFiles(sessionFiles, DateTime.UtcNow, TimeSpan.FromDays(1)))
            {
                try { File.Delete(stale); } catch { /* best-effort */ }
            }
        }
        catch
        {
            // Best-effort housekeeping - never let it interfere with launch.
        }
    }

    /// <summary>Hands the currently-authenticated SU identity (the same one this session's own AD
    /// writes already use - see IAdService.Credentials) to the offboarding script via its
    /// -CredentialFile parameter, so the script's Disable-ADAccount/Set-ADAccountPassword/etc.
    /// calls run as that account too, instead of silently defaulting to whatever Windows session
    /// launched Inventory Manager. Found live 2026-09-16: an operator whose SU login succeeded
    /// still got "Insufficient access rights" from the offboarding tool, because the tool never
    /// received any credential and fell back to the operator's own (non-delegated) Windows logon.
    ///
    /// The script expects a path to a PSCredential exported via Export-Clixml (DPAPI-encrypted,
    /// readable only by this same Windows account) and deletes that file itself once it's read it
    /// - a one-shot handoff. To build that file, this shells out to a short, fixed PowerShell
    /// command (no interpolated/untrusted text in the command string itself - the plaintext
    /// password and username are piped over that child process's stdin, never placed on a command
    /// line or in an environment variable, matching this app's existing arg-injection discipline;
    /// the output path travels via an environment variable instead, since it isn't secret and
    /// avoids any quoting concern). Returns null (script falls back to its own Credential
    /// Manager/interactive prompt, same as today) when no SU/override credential is active - i.e.
    /// the operator chose "Use My Windows Session for AD" - or if the export itself fails for any
    /// reason; either way this must never block the offboarding tool from launching at all.</summary>
    private string? TryExportSuCredentialFile()
    {
        var credentials = _adService.Credentials;
        if (credentials is null)
        {
            return null;
        }

        var tempPath = Path.Combine(Path.GetTempPath(), $"{TempCredentialCleanup.OffboardCredentialPrefix}{Guid.NewGuid():N}.xml");
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                RedirectStandardInput = true,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(
                "$u = [Console]::In.ReadLine(); $p = [Console]::In.ReadLine(); " +
                "$sec = ConvertTo-SecureString $p -AsPlainText -Force; " +
                "$cred = New-Object System.Management.Automation.PSCredential($u, $sec); " +
                "$cred | Export-Clixml -Path $env:INVMGR_CRED_OUT");
            startInfo.EnvironmentVariables["INVMGR_CRED_OUT"] = tempPath;

            using var exportProcess = Process.Start(startInfo)!;
            exportProcess.StandardInput.WriteLine(credentials.Username);
            exportProcess.StandardInput.WriteLine(credentials.ToNetworkCredential().Password);
            exportProcess.StandardInput.Close();
            if (!exportProcess.WaitForExit(15_000) || exportProcess.ExitCode != 0 || !File.Exists(tempPath))
            {
                Log("Could not hand off SU credentials to the offboarding tool - it will fall back to its own credential prompt/Credential Manager entry.", AppLogLevel.Warning);
                return null;
            }
            return tempPath;
        }
        catch (Exception ex)
        {
            Log($"Could not hand off SU credentials to the offboarding tool: {ex.Message} - it will fall back to its own credential prompt/Credential Manager entry.", AppLogLevel.Warning);
            try { if (File.Exists(tempPath)) { File.Delete(tempPath); } } catch { /* best-effort cleanup */ }
            return null;
        }
    }

    /// <summary>Runs once Offboarding2026.ps1's window closes. Reads the session record the tool
    /// wrote and only offers the device/computer side (Finish) - or Undo - when it shows a REAL
    /// run for this device's own user; a window closed without running, test runs only, a run
    /// against a different user, or no record at all (script blocked/crashed) changes nothing.
    /// Always re-fetches the row by Id rather than closing over any earlier reference: the
    /// periodic 15s refresh (or even this method's own dialogs pumping messages) can reload
    /// _store.Items - and every InventoryItem instance with it - while the external tool or these
    /// dialogs are open.</summary>
    private void OnOffboardingToolClosed(int itemId, string deviceName, string username, string sessionPath)
    {
        var handedOffToUndo = false;
        try
        {
            var outcome = OffboardingOutcomeEvaluator.Evaluate(
                OffboardingOutcomeEvaluator.TryParseSession(TryReadAllText(sessionPath)), username);

            ResolveScheduledTerminationAfterRun(username, outcome);

            switch (outcome.Kind)
            {
                case OffboardingOutcomeKind.NoResult:
                    Log($"The offboarding tool for '{username}' closed without reporting anything (crashed, was killed, or failed before it started) - '{deviceName}' and its computer were left unchanged.", AppLogLevel.Warning);
                    MessageBox.Show(this,
                        $"The offboarding tool for {username} closed without reporting a result - it may have crashed or been closed forcibly.\r\n\r\nNothing was changed for '{deviceName}'. If the tool did run, its own log is in C:\\Temp\\Offboarding\\ on this PC.",
                        "Offboard User", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                case OffboardingOutcomeKind.NothingRan:
                    Log($"Offboarding tool closed for '{username}' - no real (non-test) run happened, so '{deviceName}' was left unchanged.");
                    return;
                case OffboardingOutcomeKind.OtherUserOnly:
                    var others = string.Join(", ", outcome.OtherUsers);
                    Log($"The offboarding tool opened for '{username}' (device '{deviceName}') was run against {others} instead - '{deviceName}' was deliberately left unchanged.", AppLogLevel.Warning);
                    MessageBox.Show(this,
                        $"The offboarding tool was run for {others}, not for {username}.\r\n\r\n'{deviceName}' belongs to {username}, so it and its computer were left unchanged. If {username} still needs offboarding, use Offboard User again.",
                        "Offboard User", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
            }

            if (outcome.Kind == OffboardingOutcomeKind.CompletedWithErrors)
            {
                Log($"Offboarding tool for '{username}' finished with problems: {string.Join(" | ", outcome.FailedSteps)}", AppLogLevel.Warning);
            }

            while (true)
            {
                using var dialog = new OffboardingDecisionDialog(deviceName, username, outcome);
                dialog.ShowDialog(this);
                switch (dialog.Decision)
                {
                    case OffboardingDecision.Finish:
                        FinishOffboardingSync(itemId, deviceName, username, outcome.Ticket);
                        return;
                    case OffboardingDecision.Undo:
                        if (!ConfirmOffboardingUndo(deviceName, username, outcome))
                        {
                            continue;   // back to the three choices
                        }
                        handedOffToUndo = StartOffboardingUndo(itemId, deviceName, username, sessionPath, outcome.Ticket);
                        return;
                    default:
                        Log($"Offboarding tool closed for '{deviceName}' ({username}) - finish/undo deferred by operator; nothing changed on the device or its computer.");
                        return;
                }
            }
        }
        finally
        {
            // The undo keeps the record locked and still needs the session file - it cleans both
            // up itself when the undo window closes.
            if (!handedOffToUndo)
            {
                TryDeleteFile(sessionPath);
                ReleaseOffboardLock(itemId);
            }
        }
    }

    private bool ConfirmOffboardingUndo(string deviceName, string username, OffboardingOutcome outcome)
    {
        var text = $"Undo the offboarding of {username}?\r\n\r\n" +
                   $"This reverses the {outcome.UndoableChangeCount} change(s) the offboarding tool made: the account is re-enabled, and its groups, OU, description, mailbox settings, permissions and licenses are put back the way they were.\r\n\r\n";
        if (outcome.NotUndoableNotes.Count > 0)
        {
            text += "It can't reverse:\r\n" + string.Join("\r\n", outcome.NotUndoableNotes.Select(n => " - " + n)) + "\r\n\r\n";
        }
        text += $"'{deviceName}' and its computer are not touched.\r\n\r\nSign-in windows for Exchange / Microsoft Graph / SharePoint may appear.";
        return MessageBox.Show(this, text, "Undo Offboarding", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
    }

    /// <summary>Launches the script's undo mode against the session record. Returns true when the
    /// undo is running - it then owns the record lock and both temp files until it closes.</summary>
    private bool StartOffboardingUndo(int itemId, string deviceName, string username, string sessionPath, string? ticket)
    {
        var reportPath = Path.Combine(Path.GetTempPath(), $"{OffboardingOutcomeEvaluator.UndoReportFilePrefix}{Guid.NewGuid():N}.json");
        var launched = LaunchOffboardingScript(
            ["-UndoFrom", sessionPath, "-UndoUser", username, "-ResultFile", reportPath],
            () => OnOffboardingUndoClosed(itemId, deviceName, username, sessionPath, reportPath, ticket));
        if (!launched)
        {
            return false;
        }
        Log($"Undo of the offboarding for '{username}' started - '{deviceName}' is left as it is.", AppLogLevel.Warning);
        return true;
    }

    private void OnOffboardingUndoClosed(int itemId, string deviceName, string username, string sessionPath, string reportPath, string? ticket)
    {
        try
        {
            var report = OffboardingOutcomeEvaluator.TryParseUndoReport(TryReadAllText(reportPath));
            if (report is null)
            {
                Log($"The undo tool for '{username}' closed without reporting a result - check C:\\Temp\\Offboarding\\Undo_{username}_*.log on this PC.", AppLogLevel.Error);
                _audit.Record(SecurityEventType.OffboardingUndone, SecuritySeverity.Warning, _suUsername, username,
                    $"Undo of the offboarding for '{username}' (device '{deviceName}') was started but reported no result.");
                MessageBox.Show(this,
                    $"The undo tool for {username} closed without reporting a result, so it's unknown what (if anything) was restored.\r\n\r\nCheck C:\\Temp\\Offboarding\\Undo_{username}_*.log on this PC. '{deviceName}' was not changed.",
                    "Undo Offboarding", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            foreach (var line in report.Undone) { Log($"Undo ({username}) restored - {line}"); }
            foreach (var line in report.Failed) { Log($"Undo ({username}) FAILED - {line}", AppLogLevel.Error); }
            foreach (var line in report.NotUndoable) { Log($"Undo ({username}) can't reverse - {line}", AppLogLevel.Warning); }

            var summary = $"{report.Undone.Count} restored, {report.Failed.Count} failed, {report.NotUndoable.Count} can't be undone";
            if (report.CompletedAt is null)
            {
                summary += " (the undo was interrupted before it finished)";
            }
            _audit.Record(SecurityEventType.OffboardingUndone, SecuritySeverity.Warning, _suUsername, username,
                $"Offboarding of '{username}' undone (device '{deviceName}', ticket #{ticket ?? "n/a"}): {summary}.");
            try
            {
                AdNoteAppender.Append(_adService, username, $"Offboarding undone by {_suUsername}{(string.IsNullOrEmpty(ticket) ? "" : $" - Ticket # {ticket}")}: {summary}.");
            }
            catch (Exception ex)
            {
                Log($"Undo finished for '{username}', but the AD Notes entry could NOT be written: {ex.Message}", AppLogLevel.Warning);
            }

            static string Section(string title, IReadOnlyList<string> items)
            {
                if (items.Count == 0) { return ""; }
                var shown = items.Take(12).Select(i => " - " + i).ToList();
                if (items.Count > 12) { shown.Add($" - ...and {items.Count - 12} more (see the Activity Log)"); }
                return $"{title}\r\n{string.Join("\r\n", shown)}\r\n\r\n";
            }
            var message = $"Undo finished for {username}: {summary}.\r\n\r\n" +
                          Section("Could not be restored - fix these by hand:", report.Failed) +
                          Section("Can't be undone - follow up:", report.NotUndoable) +
                          $"'{deviceName}' and its computer were not changed.";
            MessageBox.Show(this, message, "Undo Offboarding", MessageBoxButtons.OK,
                report.Failed.Count > 0 || report.CompletedAt is null ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }
        finally
        {
            TryDeleteFile(sessionPath);
            TryDeleteFile(reportPath);
            ReleaseOffboardLock(itemId);
        }
    }

    // ---- Scheduled terminations ----
    // Nothing here ever runs an offboarding by itself. A due entry only PROMPTS the admin who has
    // the app open; "Offboard Now" then starts the normal supervised tool via StartOffboarding, so
    // it uses that admin's own SU login and Microsoft sign-in and no credential is ever stored.

    private string ScheduledTerminationsMenuText()
    {
        try
        {
            var pending = _scheduledTerminations.LoadPending();
            if (pending.Count == 0)
            {
                return "Scheduled Terminations...";
            }
            var due = pending.Count(t => ScheduledTerminationRules.IsDue(t, DateTime.UtcNow));
            return due > 0
                ? $"Scheduled Terminations... ({pending.Count} pending, {due} due)"
                : $"Scheduled Terminations... ({pending.Count} pending)";
        }
        catch
        {
            return "Scheduled Terminations...";
        }
    }

    private bool HasPendingScheduledTermination(string username)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return false;
        }
        try { return _scheduledTerminations.FindPendingForUser(username.Trim()) is not null; }
        catch { return false; }
    }

    private void InvokeScheduleTermination(string statusKey)
    {
        const string title = "Schedule Termination";
        var selected = GetSelectedItem(statusKey);
        if (selected is null)
        {
            MessageBox.Show(this, "Select exactly one device to schedule its user's termination.", title, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var username = selected.Username.Trim();
        if (string.IsNullOrWhiteSpace(username))
        {
            MessageBox.Show(this, $"'{selected.DeviceName}' has no linked AD username. Use 'Match User' to link this device to a real AD account first.", title, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (HoneytokenPolicy.IsHoneytokenUser(username))
        {
            _audit.Record(SecurityEventType.HoneytokenTripped, SecuritySeverity.Critical, _suUsername, username,
                $"Termination scheduling attempted against honeytoken account '{username}'. Refused; nothing changed.");
            Log($"Termination scheduling attempted against honeytoken account '{username}' - refused and alerted.", AppLogLevel.Error);
            MessageBox.Show(this, $"'{username}' is a monitored decoy account. This attempt has been logged and alerted. Nothing was changed.", title, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        if (RefuseIfProtectedAccount(username, title))
        {
            return;
        }

        ScheduledTermination? existing;
        try
        {
            existing = _scheduledTerminations.FindPendingForUser(username);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not read the scheduled terminations: {ex.Message}", title, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        if (existing is not null)
        {
            EditScheduledTermination(existing);
            return;
        }

        using var dialog = new ScheduleTerminationDialog(selected.FullName, username, selected.DeviceName.Trim(), null);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }
        var entry = new ScheduledTermination
        {
            Username = username,
            DisplayName = selected.FullName,
            DeviceName = selected.DeviceName.Trim(),
            ItemId = selected.Id,
            DueAtUtc = dialog.DueLocal.ToUniversalTime(),
            Ticket = dialog.Ticket,
            Notes = dialog.Notes,
            ScheduledBy = _suUsername,
            ScheduledAtUtc = DateTime.UtcNow,
        };
        try
        {
            if (_scheduledTerminations.Add(entry) is null)
            {
                MessageBox.Show(this, $"Another admin just scheduled a termination for {username}. Open File → Scheduled Terminations to see or change it.", title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not save the schedule: {ex.Message}", title, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        _audit.Record(SecurityEventType.TerminationScheduled, SecuritySeverity.Info, _suUsername, username,
            $"Termination of '{username}' scheduled for {dialog.DueLocal:M/d/yyyy h:mm tt} (ticket #{entry.Ticket}, device '{entry.DeviceName}').");
        Log($"Scheduled the termination of '{username}' for {dialog.DueLocal:M/d/yyyy h:mm tt} (ticket #{entry.Ticket}).");
        MessageBox.Show(this,
            $"Termination of {username} scheduled for {dialog.DueLocal:dddd, M/d/yyyy 'at' h:mm tt}.\r\n\r\nNothing happens automatically - at that time, the next admin with Inventory Manager open (or opening it) is prompted to offboard.",
            title, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>Edit (or remove, from inside the dialog) a pending schedule. Returns true when the
    /// stored list may have changed, so a list dialog should reload.</summary>
    private bool EditScheduledTermination(ScheduledTermination entry)
    {
        const string title = "Edit Scheduled Termination";
        using var dialog = new ScheduleTerminationDialog(entry.DisplayName, entry.Username, entry.DeviceName, entry);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return false;
        }
        if (dialog.RemoveRequested)
        {
            return CancelScheduledTermination(entry);
        }
        try
        {
            if (!_scheduledTerminations.Reschedule(entry.Id, dialog.DueLocal.ToUniversalTime(), dialog.Ticket, dialog.Notes))
            {
                MessageBox.Show(this, $"The scheduled termination for {entry.Username} is no longer pending - another admin may have completed or cancelled it. Nothing was changed.", title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return true;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not save the change: {ex.Message}", title, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        _audit.Record(SecurityEventType.TerminationScheduleChanged, SecuritySeverity.Info, _suUsername, entry.Username,
            $"Scheduled termination of '{entry.Username}' changed to {dialog.DueLocal:M/d/yyyy h:mm tt} (ticket #{dialog.Ticket}).");
        Log($"Changed the scheduled termination of '{entry.Username}' to {dialog.DueLocal:M/d/yyyy h:mm tt} (ticket #{dialog.Ticket}).");
        return true;
    }

    private bool CancelScheduledTerminationWithConfirm(ScheduledTermination entry)
    {
        var answer = MessageBox.Show(this,
            $"Cancel the scheduled termination for {entry.Username}?\r\n\r\nNothing is changed on the account.",
            "Cancel Scheduled Termination", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        return answer == DialogResult.Yes && CancelScheduledTermination(entry);
    }

    private bool CancelScheduledTermination(ScheduledTermination entry)
    {
        try
        {
            if (!_scheduledTerminations.Cancel(entry.Id, _suUsername, DateTime.UtcNow, $"Cancelled by {_suUsername}."))
            {
                MessageBox.Show(this, $"The scheduled termination for {entry.Username} was already completed or cancelled. Nothing was changed.", "Cancel Scheduled Termination", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return true;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not cancel the schedule: {ex.Message}", "Cancel Scheduled Termination", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        _audit.Record(SecurityEventType.TerminationScheduleCancelled, SecuritySeverity.Info, _suUsername, entry.Username,
            $"Scheduled termination of '{entry.Username}' (ticket #{entry.Ticket}) cancelled.");
        Log($"Cancelled the scheduled termination of '{entry.Username}' (ticket #{entry.Ticket}).");
        return true;
    }

    private void ShowScheduledTerminations(string? dueHeadline)
    {
        using var dialog = new ScheduledTerminationsDialog(_scheduledTerminations, _suUsername, dueHeadline, EditScheduledTermination, CancelScheduledTerminationWithConfirm);
        dialog.ShowDialog(this);
        if (dialog.OffboardRequested is { } chosen)
        {
            OffboardScheduledTermination(chosen);
        }
    }

    /// <summary>Prompts when a scheduled termination is due and nobody else is already handling
    /// it. Runs at launch and from the 15s timer; after showing, stays quiet for
    /// <see cref="TerminationPromptRepeat"/> so closing the dialog doesn't mean being nagged every
    /// 15 seconds, but a still-due entry does come back.</summary>
    private void CheckDueTerminations()
    {
        if (_terminationPromptOpen || IsDisposed || !IsHandleCreated || !Visible || !Enabled
            || DateTime.UtcNow < _nextTerminationPromptUtc
            || Application.OpenForms.Cast<Form>().Any(f => !ReferenceEquals(f, this) && f.Visible))
        {
            return;
        }

        IReadOnlyList<ScheduledTermination> due;
        try
        {
            due = ScheduledTerminationRules.DueForPrompt(_scheduledTerminations.Load(), _suUsername, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            Log($"Could not check scheduled terminations: {ex.Message}", AppLogLevel.Warning);
            _nextTerminationPromptUtc = DateTime.UtcNow + TerminationPromptRepeat;
            return;
        }
        if (due.Count == 0)
        {
            return;
        }

        _terminationPromptOpen = true;
        try
        {
            var names = string.Join(", ", due.Take(4).Select(t => t.Username)) + (due.Count > 4 ? $" and {due.Count - 4} more" : "");
            var headline = due.Count == 1
                ? $"A scheduled termination is due: {names}. Offboard now, or close this to be reminded again in 30 minutes."
                : $"{due.Count} scheduled terminations are due: {names}. Offboard now, or close this to be reminded again in 30 minutes.";
            Log($"Scheduled termination due: {names}.", AppLogLevel.Warning);
            ShowScheduledTerminations(headline);
        }
        finally
        {
            _terminationPromptOpen = false;
            _nextTerminationPromptUtc = DateTime.UtcNow + TerminationPromptRepeat;
        }
    }

    /// <summary>Starts the normal offboarding for a scheduled entry. Claims it first so a second
    /// admin who opens the app meanwhile isn't prompted for the same person; the claim is dropped
    /// again if the tool can't start, and the entry is completed when the tool reports a real run
    /// (see ResolveScheduledTerminationAfterRun).</summary>
    private void OffboardScheduledTermination(ScheduledTermination entry)
    {
        const string title = "Offboard User";
        try
        {
            if (!_scheduledTerminations.TryClaim(entry.Id, _suUsername, DateTime.UtcNow))
            {
                MessageBox.Show(this, $"The scheduled termination for {entry.Username} was just completed, cancelled, or picked up by another admin. Nothing was started.", title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not claim the scheduled termination: {ex.Message}", title, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var item = _store.Items.FirstOrDefault(i => i.Id == entry.ItemId && string.Equals(i.Username.Trim(), entry.Username, StringComparison.OrdinalIgnoreCase))
                   ?? _store.Items.Where(i => string.Equals(i.Username.Trim(), entry.Username, StringComparison.OrdinalIgnoreCase))
                                  .OrderBy(i => i.Status == "Current" ? 0 : 1).FirstOrDefault();
        if (item is null)
        {
            ReleaseScheduledTerminationClaim(entry);
            MessageBox.Show(this,
                $"No inventory record is linked to {entry.Username} any more (the device may have been deleted or re-linked), so the offboarding tool can't be started from here.\r\n\r\nThe schedule is still pending. Find {entry.Username}'s device and use Offboard User, or Cancel the schedule.",
                title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Log($"Starting the scheduled termination of '{entry.Username}' (ticket #{entry.Ticket}, scheduled by {entry.ScheduledBy}).", AppLogLevel.Warning);
        if (!StartOffboarding(item))
        {
            ReleaseScheduledTerminationClaim(entry);
        }
    }

    private void ReleaseScheduledTerminationClaim(ScheduledTermination entry)
    {
        try { _scheduledTerminations.ReleaseClaim(entry.Id, _suUsername); }
        catch (Exception ex) { Log($"Could not release the claim on the scheduled termination of '{entry.Username}': {ex.Message}", AppLogLevel.Warning); }
    }

    /// <summary>Called when the offboarding tool closes. A real run for the user (however it was
    /// launched - from the schedule or the plain Offboard User command) completes that user's
    /// pending schedule; anything else just hands back a claim this session was holding.</summary>
    private void ResolveScheduledTerminationAfterRun(string username, OffboardingOutcome outcome)
    {
        try
        {
            var pending = _scheduledTerminations.FindPendingForUser(username);
            if (pending is null)
            {
                return;
            }
            if (outcome.Kind is not (OffboardingOutcomeKind.Succeeded or OffboardingOutcomeKind.CompletedWithErrors))
            {
                _scheduledTerminations.ReleaseClaim(pending.Id, _suUsername);
                return;
            }
            var note = outcome.Kind == OffboardingOutcomeKind.Succeeded
                ? "Offboarding tool completed with no errors."
                : $"Offboarding tool ran with problems: {string.Join(" | ", outcome.FailedSteps)}";
            if (_scheduledTerminations.CompletePendingForUser(username, _suUsername, DateTime.UtcNow, note) is not null)
            {
                _audit.Record(SecurityEventType.TerminationScheduleCompleted, SecuritySeverity.Info, _suUsername, username,
                    $"Scheduled termination of '{username}' (ticket #{pending.Ticket}) completed by {_suUsername}. {note}");
                Log($"Scheduled termination of '{username}' marked completed. {note}");
            }
        }
        catch (Exception ex)
        {
            Log($"Could not update the scheduled termination for '{username}': {ex.Message}", AppLogLevel.Warning);
        }
    }

    private void ReleaseOffboardLock(int itemId)
    {
        var lockRow = _store.Items.FirstOrDefault(i => i.Id == itemId);
        if (lockRow is not null)
        {
            RecordEditLock.Release(lockRow, _suUsername);
        }
        SaveAndRefresh();
    }

    private static string? TryReadAllText(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch { return null; }
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); } catch { /* best-effort - the startup sweep catches leftovers */ }
    }

    /// <summary>The device/computer half of offboarding, after a real run for this user: disable
    /// and move the computer, clear the row's user fields, move it to Disabled Devices, and note it
    /// on the user's AD account. The ticket comes from the tool's own run when it has one.</summary>
    private void FinishOffboardingSync(int itemId, string deviceName, string username, string? ticketFromTool)
    {
        InventoryItem? FetchRow() => _store.Items.FirstOrDefault(i => i.Id == itemId);
        if (FetchRow() is null)
        {
            MessageBox.Show(this, $"'{deviceName}' no longer exists in the inventory - it may have been deleted by another session. Nothing was updated.", "Offboard User", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var ticket = ticketFromTool;
        if (string.IsNullOrWhiteSpace(ticket))
        {
            using var ticketDialog = new SimpleTextPromptDialog("Ticket #", $"Enter the ticket number for offboarding '{deviceName}':");
            if (ticketDialog.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(ticketDialog.Value))
            {
                MessageBox.Show(this, "No ticket number entered - the inventory record was NOT updated. You can mark it Disabled Devices manually via Edit Item or Move to.", "Offboard User", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            ticket = ticketDialog.Value;
        }

        var currentRow = FetchRow();
        if (currentRow is null)
        {
            MessageBox.Show(this, $"'{deviceName}' no longer exists in the inventory - it may have been deleted by another session. Nothing was updated.", "Offboard User", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var disabledDescription = $"Disabled per ticket # {ticket}";
        try
        {
            _adService.SetComputerEnabled(deviceName, false);
            _adService.SetComputerDescription(deviceName, disabledDescription);
            MoveComputerIntoDisabledOuIfNeeded(deviceName);
            Log($"Computer '{deviceName}' disabled and Description updated (ticket #{ticket}).");
        }
        catch (Exception ex)
        {
            Log($"'{deviceName}' offboarding tool completed, but the computer object could NOT be updated in AD: {ex.Message}", AppLogLevel.Error);
            MessageBox.Show(this, $"Could not update the computer object in AD: {ex.Message}\r\n\r\nThe inventory record will still be moved to Disabled Devices.", "Offboard User", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        if (!string.IsNullOrWhiteSpace(username))
        {
            try
            {
                AdNoteAppender.Append(_adService, username, $"Offboarded by {_suUsername} - Ticket # {ticket}. Computer '{deviceName}' disabled and moved to Disabled Devices.");
            }
            catch (Exception ex)
            {
                Log($"'{deviceName}' offboarding finished, but the AD Notes entry could NOT be written for '{username}': {ex.Message}", AppLogLevel.Warning);
            }
        }

        currentRow.Status = "DisabledDevices";
        currentRow.ADComputerDisabled = "Computer object disabled in AD";
        currentRow.ADFlagReason = "";
        currentRow.Description = disabledDescription;
        currentRow.Username = "";
        currentRow.FirstName = "";
        currentRow.LastName = "";
        currentRow["CellPhone"] = "";
        currentRow["DeviceModel"] = "";

        Log($"Offboarding finished: '{deviceName}' disabled (ticket #{ticket}), assigned-user fields cleared, moved to Disabled Devices, and AD Notes updated.", AppLogLevel.Info);
    }

    private TabPage BuildTab(string statusKey, string statusLabel, IReadOnlyList<MoveTarget> moveTargets, bool hideCreateButtons)
    {
        var tabPage = new TabPage { Text = statusLabel };

        var topPanel = new Panel { Dock = DockStyle.Top, Height = 40 };
        var lblSearch = new Label { Text = "Search:", Location = new Point(10, 12), Size = new Size(50, 20) };
        var txtSearch = new TextBox { Location = new Point(65, 9), Size = new Size(300, 20) };
        _searchBoxes[statusKey] = txtSearch;
        topPanel.Controls.Add(lblSearch);
        topPanel.Controls.Add(txtSearch);

        var btnClearSearch = new Button { Text = "✕", Location = new Point(368, 8), Size = new Size(24, 22) };
        btnClearSearch.Click += (_, _) => txtSearch.Clear();
        topPanel.Controls.Add(btnClearSearch);

        var lblStatus = new Label { Location = new Point(400, 12), Size = new Size(850, 20), ForeColor = Color.DarkRed };
        _statusLabels[statusKey] = lblStatus;
        topPanel.Controls.Add(lblStatus);

        var grid = new DoubleBufferedDataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = true,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
            RowHeadersVisible = false,
            AllowUserToResizeRows = false,
            AutoGenerateColumns = false,
        };
        // Explicit fixed columns, in exactly InventorySchema.GridColumns' order - everything else
        // the underlying DataTable carries (Id, every non-grid field) simply has no grid column at
        // all, so there's no hide-and-reorder dance to get wrong. The full record is still reached
        // via double-click/right-click Edit.
        foreach (var (key, header) in InventorySchema.GridColumns)
        {
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = key, DataPropertyName = key, HeaderText = header, Width = 130 });
        }
        _grids[statusKey] = grid;

        var contextMenu = new ContextMenuStrip();
        var menuEdit = new ToolStripMenuItem("Edit Item");
        menuEdit.Click += (_, _) => EditSelectedItem(statusKey);
        contextMenu.Items.Add(menuEdit);
        var menuDelete = new ToolStripMenuItem("Delete Item");
        menuDelete.Click += (_, _) => DeleteSelectedItems(statusKey);
        contextMenu.Items.Add(menuDelete);

        // Francois, 2026-09-23: "Current Inventory" pulled out of the "Move to" submenu into its
        // own top-level item - promoting a device back into active service is common enough (and
        // distinct enough, given the Edit Item prompt + AD re-enable it triggers) to deserve its
        // own line rather than being buried a level down alongside every other destination.
        var currentTarget = moveTargets.FirstOrDefault(t => !t.IsDomainMove && t.StatusKey == "Current");
        if (currentTarget is not null)
        {
            var menuMoveToCurrent = new ToolStripMenuItem("Move to Current Inventory");
            menuMoveToCurrent.Click += (_, _) => MoveSelectedItems(statusKey, "Current");
            contextMenu.Items.Add(menuMoveToCurrent);
        }

        var menuMoveTo = new ToolStripMenuItem("Move to");
        foreach (var target in moveTargets.Where(t => t != currentTarget))
        {
            var menuMoveTarget = new ToolStripMenuItem(target.Label);
            menuMoveTarget.Click += (_, _) =>
            {
                if (target.IsDomainMove)
                {
                    SetDeviceDomain(statusKey, target.DomainValue!, target.Label);
                }
                else
                {
                    MoveSelectedItems(statusKey, target.StatusKey!);
                }
            };
            menuMoveTo.DropDownItems.Add(menuMoveTarget);
        }
        contextMenu.Items.Add(menuMoveTo);

        contextMenu.Items.Add(new ToolStripSeparator());

        var menuCheckAd = new ToolStripMenuItem("Check AD");
        menuCheckAd.Click += (_, _) => InvokeAdCheckForSelected(statusKey);
        contextMenu.Items.Add(menuCheckAd);
        // Direct manual override (Francois, 2026-09-18) - just flips the computer object's
        // userAccountControl bit in AD, nothing else (no OU move, no inventory tab change) - for
        // ad-hoc troubleshooting rather than any of the tab-driven auto-enable/disable flows
        // elsewhere in this class (Move to, Offboard User, Transfer User's computer handoff).
        var menuToggleComputerEnabled = new ToolStripMenuItem("Disable Device");
        menuToggleComputerEnabled.Click += (_, _) => InvokeToggleComputerEnabled(statusKey);
        contextMenu.Items.Add(menuToggleComputerEnabled);
        var menuMatchUser = new ToolStripMenuItem("Match User");
        menuMatchUser.Click += (_, _) => InvokeMatchUserToAd(statusKey);
        contextMenu.Items.Add(menuMatchUser);
        var menuTransferUser = new ToolStripMenuItem("Transfer User");
        menuTransferUser.Click += (_, _) => InvokeTransferUser(statusKey);
        contextMenu.Items.Add(menuTransferUser);
        var menuCopyPermissions = new ToolStripMenuItem("Copy This User's Permissions...");
        menuCopyPermissions.Click += (_, _) => InvokeCopyPermissionsFromRow(statusKey);
        contextMenu.Items.Add(menuCopyPermissions);
        var menuOffboardUser = new ToolStripMenuItem("Offboard User");
        menuOffboardUser.Click += (_, _) => InvokeOffboardUser(statusKey);
        contextMenu.Items.Add(menuOffboardUser);
        // Schedule it for later instead - see ScheduledTermination. Nothing runs by itself; the
        // next admin with the app open is prompted when it's due.
        var menuScheduleTermination = new ToolStripMenuItem("Schedule Termination...");
        menuScheduleTermination.Click += (_, _) => InvokeScheduleTermination(statusKey);
        contextMenu.Items.Add(menuScheduleTermination);

        contextMenu.Items.Add(new ToolStripSeparator());

        // "Pull the log for this record" - Francois, 2026-09-16: the Inventory Change Log (Security
        // menu) already records every field change forever, unbounded by AD Notes' ~1000-char cap -
        // this just filters that same ledger down to one device instead of making him hunt through
        // "View Recent Inventory Changes" for it.
        var menuChangeHistory = new ToolStripMenuItem("View Change History...");
        menuChangeHistory.Click += (_, _) => ShowDeviceChangeHistory(statusKey);
        contextMenu.Items.Add(menuChangeHistory);

        contextMenu.Items.Add(new ToolStripSeparator());

        // One toggle instead of two opposite fixed-action items - label reflects the FIRST
        // selected row's current state and clicking it flips ALL selected rows to the opposite.
        var menuToggleHighlight = new ToolStripMenuItem("Ignore Highlight Color");
        menuToggleHighlight.Click += (_, _) => InvokeToggleHighlight(statusKey);
        contextMenu.Items.Add(menuToggleHighlight);

        // Francois, 2026-09-17: a per-row opt-out from Active Directory Sync (File menu) for a
        // known-good exception it would otherwise flag/relocate - e.g. a shared/kiosk device with
        // no real assigned user, or a computer intentionally left disabled while still in active
        // use. Independent of Ignore Highlight Color above (that only suppresses row coloring; this
        // actually stops AD Sync from touching the row at all - see AdUserMatchCheckService.Run).
        // Deliberately does NOT affect the separate "Check AD" action above, which is an explicit,
        // one-off manual check the operator asked for on this specific row.
        var menuToggleAdSync = new ToolStripMenuItem("Exclude from AD Sync");
        menuToggleAdSync.Click += (_, _) => InvokeToggleAdSync(statusKey);
        contextMenu.Items.Add(menuToggleAdSync);

        // Francois, 2026-09-24: a quick one-click way to file a record as a server - see
        // MarkSelectedAsServer for exactly what it does. Disabled on the Servers tab itself since
        // there's nothing to do there (already marked).
        var menuMarkAsServer = new ToolStripMenuItem("Mark as Server");
        menuMarkAsServer.Click += (_, _) => MarkSelectedAsServer(statusKey);
        contextMenu.Items.Add(menuMarkAsServer);

        _contextMenuValidRow[statusKey] = false;
        contextMenu.Opening += (_, e) =>
        {
            var count = grid.SelectedRows.Count;
            e.Cancel = count == 0 || !_contextMenuValidRow[statusKey];
            if (e.Cancel)
            {
                return;
            }
            menuEdit.Text = count > 1 ? $"Edit ({count})" : "Edit Item";
            menuDelete.Text = count > 1 ? $"Delete ({count})" : "Delete Item";
            menuEdit.Enabled = count == 1;
            menuTransferUser.Enabled = count == 1;
            menuCopyPermissions.Enabled = count == 1;
            menuOffboardUser.Enabled = count == 1;
            menuScheduleTermination.Enabled = count == 1;
            menuChangeHistory.Enabled = count == 1;
            var firstItem = GetSelectedItem(statusKey) ?? _store.Items.FirstOrDefault(i => i.Id == (int)((DataRowView)grid.SelectedRows[0].DataBoundItem!).Row["Id"]);
            menuScheduleTermination.Text = count == 1 && firstItem is not null && HasPendingScheduledTermination(firstItem.Username)
                ? "Edit Scheduled Termination..."
                : "Schedule Termination...";
            var currentlyIgnored = firstItem?.IgnoreHighlight == "Yes";
            menuToggleHighlight.Text = currentlyIgnored ? "Show Highlight Color" : "Ignore Highlight Color";
            var currentlyExcludedFromAdSync = firstItem?.IgnoreAdSync == "Yes";
            menuToggleAdSync.Text = currentlyExcludedFromAdSync ? "Include in AD Sync" : "Exclude from AD Sync";
            var currentlyDisabledInAd = !string.IsNullOrWhiteSpace(firstItem?.ADComputerDisabled);
            menuToggleComputerEnabled.Text = currentlyDisabledInAd ? "Enable Device" : "Disable Device";
            menuMarkAsServer.Enabled = statusKey != "Servers";
        };
        grid.ContextMenuStrip = contextMenu;
        grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) { EditSelectedItem(statusKey); } };

        // Port of the PS build's grid MouseDown handler: a right-click on an unselected row must
        // select just that row first (WinForms doesn't do this by default), or the context menu
        // would open against whatever was selected before - a right-click on empty grid space
        // must not open the menu at all, regardless of stale selection left over from earlier.
        grid.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Right)
            {
                return;
            }
            var hit = grid.HitTest(e.X, e.Y);
            if (hit.RowIndex >= 0)
            {
                if (!grid.Rows[hit.RowIndex].Selected)
                {
                    grid.ClearSelection();
                    grid.Rows[hit.RowIndex].Selected = true;
                }
                var colIndex = hit.ColumnIndex;
                if (colIndex < 0 || !grid.Columns[colIndex].Visible)
                {
                    colIndex = grid.Columns.Cast<DataGridViewColumn>().First(c => c.Visible).Index;
                }
                grid.CurrentCell = grid.Rows[hit.RowIndex].Cells[colIndex];
                _contextMenuValidRow[statusKey] = true;
            }
            else
            {
                _contextMenuValidRow[statusKey] = false;
            }
        };

        // Row color-coding (port of the PS build's CellFormatting handler): CellFormatting fires
        // once per visible cell, so applying the same BackColor decision in every cell of a row
        // is what makes the whole row appear one color - same trick the PS build relied on.
        // Priority order matches the PS source exactly: Ignore Highlight overrides everything,
        // then computer-disabled (grey) > computer-not-found (purple) > user-not-found (red) >
        // any other AD flag (orange) > missing Serial # (pink).
        grid.CellFormatting += (_, e) =>
        {
            if (e.RowIndex < 0 || grid.Rows[e.RowIndex].DataBoundItem is not DataRowView rowView)
            {
                return;
            }

            if (grid.Columns[e.ColumnIndex].DataPropertyName == "DeviceName")
            {
                var groupCount = (string)rowView.Row["GroupCount"];
                if (!string.IsNullOrWhiteSpace(groupCount))
                {
                    var isPrimary = (string)rowView.Row["GroupPrimary"] == "Yes";
                    if (isPrimary)
                    {
                        var key = DeviceGrouping.GroupKey((string)rowView.Row["Status"], $"{e.Value}");
                        var expanded = _expandedGroups.Contains(key);
                        var extra = int.Parse(groupCount) - 1;
                        var noun = extra == 1 ? "user" : "users";
                        var arrow = expanded ? "▼" : "▶";
                        e.Value = $"{arrow} {e.Value}  (+{extra} other {noun})";
                    }
                    else
                    {
                        e.Value = "      ↳ " + e.Value;
                    }
                    e.FormattingApplied = true;
                }
            }

            if (!string.IsNullOrWhiteSpace((string)rowView.Row["IgnoreHighlight"]) || e.CellStyle is null)
            {
                return;
            }
            if (!string.IsNullOrWhiteSpace((string)rowView.Row["ADComputerDisabled"]))
            {
                e.CellStyle.BackColor = Color.FromArgb(200, 200, 200);
                return;
            }
            if (!string.IsNullOrWhiteSpace((string)rowView.Row["ADComputerNotFound"]))
            {
                e.CellStyle.BackColor = Color.FromArgb(216, 191, 255);
                return;
            }
            var adFlag = (string)rowView.Row["ADFlagReason"];
            if (adFlag == "No matching AD user found")
            {
                e.CellStyle.BackColor = Color.FromArgb(255, 150, 150);
                return;
            }
            if (!string.IsNullOrWhiteSpace(adFlag))
            {
                e.CellStyle.BackColor = Color.FromArgb(255, 224, 178);
                return;
            }
            if (string.IsNullOrWhiteSpace((string)rowView.Row["SerialNumber"]))
            {
                e.CellStyle.BackColor = Color.MistyRose;
            }
        };

        // Clicking the expand/collapse arrow on a primary row's Device cell toggles that group.
        grid.CellClick += (_, e) =>
        {
            if (e.RowIndex < 0 || grid.Columns[e.ColumnIndex].DataPropertyName != "DeviceName")
            {
                return;
            }
            if (grid.Rows[e.RowIndex].DataBoundItem is not DataRowView rowView || (string)rowView.Row["GroupPrimary"] != "Yes")
            {
                return;
            }
            ToggleDeviceGroup(DeviceGrouping.GroupKey((string)rowView.Row["Status"], (string)rowView.Row["DeviceName"]));
        };

        txtSearch.TextChanged += (_, _) => ApplyFilter(statusKey);

        var bottomPanel = new Panel { Dock = DockStyle.Bottom, Height = 45 };
        var x = 10;
        if (!hideCreateButtons)
        {
            var btnAdd = new Button { Text = "Add Item", Location = new Point(x, 8), Size = new Size(90, 28) };
            btnAdd.Click += (_, _) => AddItem(statusKey);
            bottomPanel.Controls.Add(btnAdd);
            x += 100;
        }
        var btnEdit = new Button { Text = "Edit Item", Location = new Point(x, 8), Size = new Size(90, 28) };
        var btnDelete = new Button { Text = "Delete Item", Location = new Point(x + 100, 8), Size = new Size(90, 28) };
        btnEdit.Click += (_, _) => EditSelectedItem(statusKey);
        btnDelete.Click += (_, _) => DeleteSelectedItems(statusKey);
        bottomPanel.Controls.Add(btnEdit);
        bottomPanel.Controls.Add(btnDelete);
        x += 200;

        var lblMoveTo = new Label { Text = "Move to:", Location = new Point(x, 13), Size = new Size(55, 20) };
        bottomPanel.Controls.Add(lblMoveTo);
        x += 58;

        var cmbMoveTarget = new ComboBox { Location = new Point(x, 10), Size = new Size(160, 20), DropDownStyle = ComboBoxStyle.DropDownList };
        foreach (var target in moveTargets)
        {
            cmbMoveTarget.Items.Add(target.Label);
        }
        if (cmbMoveTarget.Items.Count > 0)
        {
            cmbMoveTarget.SelectedIndex = 0;
        }
        bottomPanel.Controls.Add(cmbMoveTarget);
        x += 165;

        var btnMove = new Button { Text = "Move", Location = new Point(x, 7), Size = new Size(70, 28) };
        btnMove.Click += (_, _) =>
        {
            if (cmbMoveTarget.SelectedIndex < 0)
            {
                MessageBox.Show(this, "Choose a destination tab first.", "Move", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var targetLabel = (string)cmbMoveTarget.SelectedItem!;
            var target = moveTargets.First(t => t.Label == targetLabel);
            if (target.IsDomainMove)
            {
                SetDeviceDomain(statusKey, target.DomainValue!, target.Label);
            }
            else
            {
                MoveSelectedItems(statusKey, target.StatusKey!);
            }
        };
        bottomPanel.Controls.Add(btnMove);
        x += 80;

        if (!hideCreateButtons)
        {
            var btnImport = new Button { Text = "Import CSV", Location = new Point(x, 8), Size = new Size(90, 28) };
            btnImport.Click += (_, _) => ImportItemsCsv(statusKey);
            bottomPanel.Controls.Add(btnImport);
            x += 100;
        }

        var btnExport = new Button { Text = "Export CSV", Location = new Point(x, 8), Size = new Size(90, 28) };
        btnExport.Click += (_, _) => ExportItemsCsv(statusKey);
        bottomPanel.Controls.Add(btnExport);
        x += 100;

        var lblLegend = new Label
        {
            Text = "Pink=NoSerial  Orange=AcctDisabled  Red=UserNotInAD  Purple=CompNotInAD  Grey=CompOff",
            Location = new Point(x, 14),
            AutoSize = true,
            ForeColor = Color.Gray,
        };
        bottomPanel.Controls.Add(lblLegend);

        tabPage.Controls.Add(grid);
        tabPage.Controls.Add(bottomPanel);
        tabPage.Controls.Add(topPanel);
        return tabPage;
    }

    private void LoadAndRefreshAll()
    {
        _store.Load();
        _lastKnownFileWriteTimeUtc = File.Exists(_store.DataFilePath) ? File.GetLastWriteTimeUtc(_store.DataFilePath) : DateTime.MinValue;
        RefreshAllTabs();
    }

    /// <summary>Every column sort, selection, and scroll position a grid had right before its
    /// DataSource gets torn down and rebuilt from scratch - see RefreshAllTabs. Keyed on the
    /// inventory Id, never a row index or DataGridViewRow reference, since neither survives a
    /// rebuild (sorting/filtering can freely change which index a given record lands on, and the
    /// old DataTable/DataView/rows are discarded entirely).</summary>
    private sealed record GridViewState(string? SortColumnKey, SortOrder SortOrder, HashSet<int> SelectedIds, int? FirstVisibleId);

    private static GridViewState CaptureGridState(DataGridView grid)
    {
        var selectedIds = grid.SelectedRows.Cast<DataGridViewRow>()
            .Select(r => (int)((DataRowView)r.DataBoundItem!).Row["Id"])
            .ToHashSet();
        int? firstVisibleId = null;
        if (grid.Rows.Count > 0 && grid.FirstDisplayedScrollingRowIndex is >= 0 and var visibleIndex
            && grid.Rows[visibleIndex].DataBoundItem is DataRowView visibleRow)
        {
            firstVisibleId = (int)visibleRow.Row["Id"];
        }
        return new GridViewState(grid.SortedColumn?.Name, grid.SortOrder, selectedIds, firstVisibleId);
    }

    /// <summary>Best-effort and non-blocking, same treatment as every other cosmetic/automatic
    /// side-effect in this class - restoring the view is never allowed to throw and break the real
    /// refresh it's piggybacking on, so every step here is wrapped defensively.</summary>
    private static void RestoreGridState(DataGridView grid, GridViewState state)
    {
        try
        {
            if (state.SortColumnKey is not null && state.SortOrder != SortOrder.None && grid.Columns[state.SortColumnKey] is { } column)
            {
                grid.Sort(column, state.SortOrder == SortOrder.Descending ? ListSortDirection.Descending : ListSortDirection.Ascending);
            }
        }
        catch { /* cosmetic only - a failed re-sort must never break the underlying refresh */ }

        try
        {
            // Find every row that matches a previously-selected Id, plus an "anchor" row to scroll
            // back into view - the first previously-selected row if there was one, otherwise
            // whatever row was merely scrolled into view before (nothing was selected, but the
            // operator's place in a long list should still not jump back to the top).
            var matchingRows = new List<DataGridViewRow>();
            DataGridViewRow? anchorRow = null;
            foreach (DataGridViewRow row in grid.Rows)
            {
                if (row.DataBoundItem is not DataRowView drv)
                {
                    continue;
                }
                var rowId = (int)drv.Row["Id"];
                if (state.SelectedIds.Contains(rowId))
                {
                    matchingRows.Add(row);
                    anchorRow ??= row;
                }
                else if (anchorRow is null && state.SelectedIds.Count == 0 && rowId == state.FirstVisibleId)
                {
                    anchorRow = row;
                }
            }

            // Assigning a brand-new DataSource leaves WinForms free to pick its own default
            // current cell/selection (typically row 0, asserted right after this call returns) -
            // set CurrentCell to the real anchor FIRST so it wins that race (this is also what
            // actually scrolls the row into view), THEN re-assert every previously-selected row's
            // .Selected afterward, since moving CurrentCell alone can clear a multi-row selection.
            if (anchorRow is not null)
            {
                grid.CurrentCell = anchorRow.Cells[0];
            }
            else
            {
                grid.ClearSelection();
            }
            foreach (var row in matchingRows)
            {
                row.Selected = true;
            }
            if (matchingRows.Count == 0 && anchorRow is not null)
            {
                grid.ClearSelection();
            }
        }
        catch { /* cosmetic only - a failed selection/scroll restore must never break the refresh */ }
    }

    /// <summary>Rebuilds every tab's grid from the current in-memory item list - called after
    /// every save, so this runs constantly (every Edit, every Move, every AD Sync, even just
    /// opening a record for editing acquires its lock via a save). Francois, 2026-09-17: "after
    /// any little thing the screen refreshes and messes up what I'm doing" - the DataTable/DataView
    /// backing every grid is thrown away and rebuilt from scratch each time (needed, since the
    /// underlying data can have genuinely changed - a row added/deleted/moved by this session or
    /// another one), which used to silently reset column sort, selection, and scroll position back
    /// to nothing on every single refresh. Now captures each grid's view state immediately before
    /// tearing it down and restores it immediately after, so a routine background refresh (or even
    /// just opening a record - see TryAcquireEditLock) no longer feels like starting over.</summary>
    private void RefreshAllTabs()
    {
        var priorStates = _baseFilters.Keys.ToDictionary(k => k, k => CaptureGridState(_grids[k]));
        var table = InventoryGridBuilder.BuildTable(_store.Items, _expandedGroups);
        foreach (var statusKey in _baseFilters.Keys)
        {
            var view = new DataView(table) { RowFilter = _baseFilters[statusKey] };
            _grids[statusKey].DataSource = view;
            ApplyFilter(statusKey);
            RestoreGridState(_grids[statusKey], priorStates[statusKey]);
        }
    }

    private void ApplyFilter(string statusKey)
    {
        UpdateTabStatusLabel(statusKey);
        if (_grids[statusKey].DataSource is not DataView view)
        {
            return;
        }
        var term = _searchBoxes[statusKey].Text.Trim();
        var baseFilter = _baseFilters[statusKey];
        if (string.IsNullOrWhiteSpace(term))
        {
            // Collapsed duplicate-group members stay hidden until expanded - but a search should
            // always be able to surface them, since "why can't I find John's laptop" would be a
            // confusing result for a feature meant to reduce clutter, not hide data.
            view.RowFilter = $"{baseFilter} AND [GroupCollapsed] <> 'Yes'";
            return;
        }
        var escaped = InventoryGridBuilder.EscapeForRowFilter(term);
        var fieldClauses = InventorySchema.VisibleFieldDefs.Select(f => $"[{f.Key}] LIKE '%{escaped}%'");
        view.RowFilter = $"{baseFilter} AND (" + string.Join(" OR ", fieldClauses) + ")";
    }

    /// <summary>Port of Update-TabStatusLabel: a fixed summary of the tab's own base filter
    /// (never affected by whatever's currently typed into that tab's search box).</summary>
    private void UpdateTabStatusLabel(string statusKey)
    {
        var rows = statusKey == OffDomainTabKey
            ? _store.Items.Where(i => i["Domain"].Trim() == "Off-Domain")
            : _store.Items.Where(i => i.Status == statusKey && i["Domain"].Trim() != "Off-Domain");
        _statusLabels[statusKey].Text = TabStatusSummaryCalculator.Compute(rows).ToString();
    }

    /// <summary>Port of Set-DeviceDomain: sets the Domain field directly (Off Domain / back On
    /// Domain) rather than moving Status - a device can be both "Current Inventory" and
    /// Off-Domain at the same time, so this is independent of Move-SelectedItems entirely.</summary>
    private void SetDeviceDomain(string statusKey, string domainValue, string domainLabel)
    {
        var items = GetSelectedItems(statusKey);
        if (items.Count == 0)
        {
            MessageBox.Show(this, "Select an item to move.", "No Selection", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var promptText = items.Count == 1
            ? $"Move '{items[0].DeviceName}' to {domainLabel}?"
            : $"Move {items.Count} selected items to {domainLabel}?";
        var confirm = MessageBox.Show(this, promptText, "Confirm Move", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (confirm != DialogResult.Yes)
        {
            return;
        }
        foreach (var item in items)
        {
            item["Domain"] = domainValue;
        }
        SaveAndRefresh();
    }

    private void RefreshFromDiskIfChanged()
    {
        if (!File.Exists(_store.DataFilePath))
        {
            return;
        }
        var writeTime = File.GetLastWriteTimeUtc(_store.DataFilePath);
        if (writeTime > _lastKnownFileWriteTimeUtc)
        {
            LoadAndRefreshAll();
            Log("Inventory refreshed automatically - picked up changes saved by another session.");
        }
    }

    private InventoryItem? GetSelectedItem(string statusKey)
    {
        var grid = _grids[statusKey];
        if (grid.SelectedRows.Count != 1)
        {
            return null;
        }
        var id = (int)((DataRowView)grid.SelectedRows[0].DataBoundItem).Row["Id"];
        return _store.Items.FirstOrDefault(i => i.Id == id);
    }

    /// <summary>Multi-select counterpart of GetSelectedItem, in on-screen row order (matches the
    /// PS build's own `Sort-Object Index` before acting on a multi-row selection).</summary>
    private List<InventoryItem> GetSelectedItems(string statusKey)
    {
        var grid = _grids[statusKey];
        var ids = grid.SelectedRows.Cast<DataGridViewRow>()
            .OrderBy(r => r.Index)
            .Select(r => (int)((DataRowView)r.DataBoundItem!).Row["Id"])
            .ToList();
        return ids.Select(id => _store.Items.FirstOrDefault(i => i.Id == id)).OfType<InventoryItem>().ToList();
    }

    private void AddItem(string statusKey)
    {
        var dialog = new ItemEditDialog("Add Item", null, _store.Items, _adService);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Result is null)
        {
            return;
        }
        var item = new InventoryItem { Id = InventorySchema.GetNextId(_store.Items), Status = statusKey };
        foreach (var (key, value) in dialog.Result)
        {
            item[key] = value;
        }
        _store.Items.Add(item);
        SaveAndRefresh();
        Log($"Added new item '{item.DeviceName}'.");
    }

    private void EditSelectedItem(string statusKey)
    {
        var selected = GetSelectedItem(statusKey);
        if (selected is null)
        {
            MessageBox.Show(this, "Select exactly one item to edit.", "Edit Item", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var item = TryAcquireEditLock(selected.Id, "edited");
        if (item is null)
        {
            return;
        }
        var deviceName = item.DeviceName;
        Log($"Opened '{deviceName}' for editing.");
        var saved = false;
        try
        {
            var currentValues = InventorySchema.VisibleFieldDefs.ToDictionary(f => f.Key, f => item[f.Key]);
            var dialog = new ItemEditDialog("Edit Item", currentValues, _store.Items, _adService);
            if (dialog.ShowDialog(this) == DialogResult.OK && dialog.Result is not null)
            {
                foreach (var (key, value) in dialog.Result)
                {
                    item[key] = value;
                }
                saved = true;
            }
            else
            {
                Log($"Closed '{deviceName}' - editing cancelled, no changes made.");
            }
        }
        finally
        {
            RecordEditLock.Release(item, _suUsername);
            SaveAndRefresh();
            if (saved)
            {
                var changeCount = _store.LastSaveFieldChanges.Count;
                Log(changeCount > 0
                    ? $"Saved '{deviceName}' - {changeCount} field(s) changed."
                    : $"Saved '{deviceName}' - no fields actually changed.");
            }
        }
    }

    private void DeleteSelectedItems(string statusKey)
    {
        var grid = _grids[statusKey];
        if (grid.SelectedRows.Count == 0)
        {
            MessageBox.Show(this, "Select one or more items to delete.", "Delete Item", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var ids = grid.SelectedRows.Cast<DataGridViewRow>()
            .Select(r => (int)((DataRowView)r.DataBoundItem!).Row["Id"])
            .ToList();
        var confirm = MessageBox.Show(this, $"Delete {ids.Count} selected item(s)? This cannot be undone.", "Confirm Delete", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (confirm != DialogResult.Yes)
        {
            return;
        }
        var deletedNames = _store.Items.Where(i => ids.Contains(i.Id)).Select(i => i.DeviceName).ToList();
        _store.Items.RemoveAll(i => ids.Contains(i.Id));
        SaveAndRefresh();
        Log($"Deleted {deletedNames.Count} item(s): {string.Join(", ", deletedNames)}.");
    }

    /// <summary>Statuses that represent a device NOT currently issued to anyone. Francois asked
    /// (2026-09-16) for two things whenever a device moves from one of these SPECIFICALLY to
    /// Current Inventory (i.e. the device is being handed to someone - NOT just leaving one of
    /// these statuses for anywhere at all, see the 2026-09-17 correction below): (1) check AD
    /// directly for whether the underlying computer object is actually disabled - never just trust
    /// InventoryItem.ADComputerDisabled, which can be stale if AD Sync hasn't run recently - and
    /// re-enable it if so, using whichever AD identity this session is authenticated as; (2) also
    /// prompt with the full Edit Item form as part of the same action so the admin fills in the new
    /// user's info instead of moving it with old/blank data and having to remember a separate edit
    /// afterward - see MoveSingleItemToCurrentWithEditPrompt. The edit prompt is deliberately
    /// single-item-only (editing several records inline isn't a pattern this app has anywhere
    /// else); a multi-select move still gets the AD re-enable check, just without the prompt.
    /// **2026-09-17 correction**: MoveSelectedItems used to gate the re-enable check on `fromStatus`
    /// alone, so it wrongly fired on ANY move away from one of these statuses regardless of
    /// destination (e.g. IT Stock -&gt; a since-removed status would have incorrectly re-enabled the
    /// computer even though the device isn't returning to service at all). Re-enabling now also
    /// requires `toStatus == "Current"` - see MoveSelectedItems and
    /// DisableComputerAndMoveToDisabledOuIfNeeded for the corresponding "every other destination
    /// pushes toward disabled instead" rule. **2026-09-23**: Terminated and Unknown removed from
    /// this set along with their tabs - ITStock and DisabledDevices are the only two "not currently
    /// issued to anyone" statuses left.</summary>
    private static readonly HashSet<string> DeviceReturnToServiceSources =
        new(StringComparer.OrdinalIgnoreCase) { "ITStock", "DisabledDevices" };

    /// <summary>Port of Move-SelectedItems: clears whichever AD flag drove the FROM tab's own
    /// auto-classification, since manually moving a row away is the human's explicit override -
    /// otherwise the next AD Sync/Check AD pass would just immediately flag it right back.</summary>
    private void MoveSelectedItems(string fromStatus, string toStatus)
    {
        var grid = _grids[fromStatus];
        if (grid.SelectedRows.Count == 0)
        {
            MessageBox.Show(this, "Select an item to move.", "No Selection", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var ids = grid.SelectedRows.Cast<DataGridViewRow>()
            .OrderBy(r => r.Index)
            .Select(r => (int)((DataRowView)r.DataBoundItem!).Row["Id"])
            .ToList();

        if (ids.Count == 1 && toStatus == "Current" && DeviceReturnToServiceSources.Contains(fromStatus))
        {
            MoveSingleItemToCurrentWithEditPrompt(fromStatus, ids[0]);
            return;
        }

        var toLabel = InventorySchema.Statuses.First(s => s.Key == toStatus).Label;
        var promptText = ids.Count == 1
            ? $"Move '{_store.Items.First(i => i.Id == ids[0]).DeviceName}' to {toLabel}?"
            : $"Move {ids.Count} selected items to {toLabel}?";
        var confirm = MessageBox.Show(this, promptText, "Confirm Move", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (confirm != DialogResult.Yes)
        {
            return;
        }
        // Re-enabling only ever happens when the destination is specifically Current Inventory
        // (Francois, 2026-09-17: "the only time a computer should be re-enabled, is when it's
        // moving from anywhere else TO the Current Inventory tab") - fixed a real bug here where
        // this used to fire on ANY move away from one of DeviceReturnToServiceSources, regardless
        // of where it was actually headed (e.g. IT Stock -> Unknown would have wrongly re-enabled
        // the computer even though the device isn't returning to service at all).
        var returningToService = toStatus == "Current" && DeviceReturnToServiceSources.Contains(fromStatus);
        var leavingDisabledOuForCurrent = fromStatus == "DisabledDevices" && toStatus == "Current";
        // Every move that does NOT land on Current Inventory pushes the computer toward disabled
        // instead - two different strengths, per Francois's own wording:
        //  - leaving Current Inventory specifically (regardless of destination): unconditional,
        //    no "already disabled" shortcut - a device stepping out of active service must always
        //    end up disabled and in the Disabled Computers OU.
        //  - any other move where neither end is Current Inventory (e.g. IT Stock -> Unknown):
        //    lazy - only act if AD actually shows it enabled right now; already-disabled is left
        //    completely alone.
        // **2026-09-24**: Servers is exempt from BOTH strengths, on either end of the move -
        // Francois: "the items are not in Current Inventory... they should not be affected." A
        // server's AD enabled state/OU placement isn't tied to a person's device lifecycle the way
        // a laptop's is, so filing something into (or out of) Servers must never touch AD at all.
        var movingAwayFromCurrent = fromStatus == "Current" && toStatus != "Current" && toStatus != "Servers";
        var movingBetweenNonCurrentTabs = fromStatus != "Current" && toStatus != "Current"
            && fromStatus != "Servers" && toStatus != "Servers";
        var movedNames = new List<string>();
        foreach (var item in _store.Items.Where(i => ids.Contains(i.Id)))
        {
            item.Status = toStatus;
            ClearAutoFlagsOnMove(item, fromStatus, toStatus);
            if (returningToService)
            {
                ReenableComputerInAdIfNeeded(item.DeviceName);
            }
            if (leavingDisabledOuForCurrent)
            {
                MoveComputerOutOfDisabledOuIfNeeded(item.DeviceName);
            }
            if (movingAwayFromCurrent)
            {
                DisableComputerAndMoveToDisabledOuIfNeeded(item.DeviceName, checkFirst: false);
            }
            else if (movingBetweenNonCurrentTabs)
            {
                DisableComputerAndMoveToDisabledOuIfNeeded(item.DeviceName, checkFirst: true);
            }
            movedNames.Add(item.DeviceName);
        }
        SaveAndRefresh();
        Log($"Moved {movedNames.Count} item(s) from {fromStatus} to {toLabel}: {string.Join(", ", movedNames)}.");
    }

    /// <summary>Edit-then-move for a single item promoted to Current Inventory from one of
    /// DeviceReturnToServiceSources. Opens the same Edit Item dialog EditSelectedItem uses,
    /// pre-filled with the record's current values, so the admin fills in the new user's info as
    /// part of the same action; clicking OK applies the edited fields, the status move, AND the AD
    /// re-enable/OU-relocate together as one action - the OK click itself serves as the move's
    /// confirmation, no separate Yes/No prompt needed. <b>2026-09-23 fix</b>: the AD re-enable/OU
    /// move used to run BEFORE the dialog was even shown, so it happened regardless of whether the
    /// admin went on to click OK or Cancel - Francois: "nothing should happen until the OK button
    /// is clicked and if anything was queued to happen once the CANCEL button is clicked, all
    /// changes are null and void." Both AD calls now happen only after a real OK - cancelling the
    /// dialog now truly leaves everything (local AND AD) exactly where it was, no exceptions.</summary>
    private void MoveSingleItemToCurrentWithEditPrompt(string fromStatus, int itemId)
    {
        var item = TryAcquireEditLock(itemId, "moved");
        if (item is null)
        {
            return;
        }
        var deviceName = item.DeviceName;
        Log($"Opened '{deviceName}' for editing (moving from {fromStatus} to Current Inventory).");
        var saved = false;
        try
        {
            var currentValues = InventorySchema.VisibleFieldDefs.ToDictionary(f => f.Key, f => item[f.Key]);
            var dialog = new ItemEditDialog($"Move '{item.DeviceName}' to Current Inventory", currentValues, _store.Items, _adService);
            if (dialog.ShowDialog(this) == DialogResult.OK && dialog.Result is not null)
            {
                foreach (var (key, value) in dialog.Result)
                {
                    item[key] = value;
                }
                item.Status = "Current";
                ClearAutoFlagsOnMove(item, fromStatus, "Current");
                ReenableComputerInAdIfNeeded(item.DeviceName);
                if (fromStatus == "DisabledDevices")
                {
                    MoveComputerOutOfDisabledOuIfNeeded(item.DeviceName);
                }
                saved = true;
            }
            else
            {
                Log($"Closed '{deviceName}' - move cancelled, item stays in {fromStatus}.");
            }
        }
        finally
        {
            RecordEditLock.Release(item, _suUsername);
            SaveAndRefresh();
            if (saved)
            {
                Log($"Moved '{deviceName}' from {fromStatus} to Current Inventory - {_store.LastSaveFieldChanges.Count} field(s) changed.");
            }
        }
    }

    /// <summary>Queries AD directly for the computer's real enabled state - never trusts the
    /// possibly-stale local ADComputerDisabled flag, which only reflects whatever the last AD Sync
    /// pass happened to see - and enables it if AD currently shows it disabled. Best-effort: a
    /// computer not found in AD, or any AD failure, is logged and otherwise ignored rather than
    /// blocking the move that triggered this check.</summary>
    private void ReenableComputerInAdIfNeeded(string deviceName)
    {
        deviceName = deviceName.Trim();
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            return;
        }
        try
        {
            var computer = _adService.GetComputer(deviceName);
            if (computer is null || computer.Enabled)
            {
                return;
            }
            _adService.SetComputerEnabled(deviceName, true);
            Log($"'{deviceName}' was disabled in Active Directory - automatically re-enabled it as part of returning this device to service.", AppLogLevel.Info);
        }
        catch (Exception ex)
        {
            Log($"Could not check/enable '{deviceName}' in Active Directory: {ex.Message}", AppLogLevel.Warning);
        }
    }

    /// <summary>Moves the AD computer object out of Disabled Computers and into the Laptop OU
    /// when a device is promoted from Disabled Devices back to Current Inventory - Francois,
    /// 2026-09-16. Only moves it if AD actually shows it sitting in Disabled Computers right now
    /// (checked by DN suffix, same pattern AdUserMatchCheckService already uses to classify a row
    /// onto the Disabled Devices tab in the first place) - a device already elsewhere (e.g. never
    /// actually relocated when it was disabled) is left alone rather than force-moved. Best-effort
    /// and non-blocking, same treatment as every other automatic AD sync in this class.</summary>
    private void MoveComputerOutOfDisabledOuIfNeeded(string deviceName)
    {
        deviceName = deviceName.Trim();
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            return;
        }
        try
        {
            var computer = _adService.GetComputer(deviceName);
            if (computer is null)
            {
                return;
            }
            var disabledOuSuffix = ("," + DisabledComputersOuDn).ToLowerInvariant();
            if (!computer.DistinguishedName.ToLowerInvariant().EndsWith(disabledOuSuffix))
            {
                return;
            }
            _adService.MoveComputer(deviceName, LaptopOuDn);
            Log($"'{deviceName}' was in the Disabled Computers OU - moved it to the Laptop OU as part of returning this device to service.", AppLogLevel.Info);
        }
        catch (Exception ex)
        {
            Log($"Could not move '{deviceName}' out of Disabled Computers in Active Directory: {ex.Message}", AppLogLevel.Warning);
        }
    }

    /// <summary>Moves the AD computer object into the Disabled Computers OU as part of offboarding
    /// - the mirror of <see cref="MoveComputerOutOfDisabledOuIfNeeded"/>, which moves a computer
    /// back out when it returns to service - Francois, 2026-09-16. Deliberately NOT its own
    /// try/catch: it runs inside OnOffboardingToolClosed's own try/catch alongside
    /// SetComputerEnabled/SetComputerDescription, so a failure here surfaces through that single
    /// "computer object could NOT be updated in AD" warning instead of a separate silent-log
    /// best-effort path - unlike the return-to-service helpers, this step is part of the one
    /// offboarding action the operator just confirmed, not an incidental side-effect of an
    /// unrelated move.</summary>
    private void MoveComputerIntoDisabledOuIfNeeded(string deviceName)
    {
        deviceName = deviceName.Trim();
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            return;
        }
        var computer = _adService.GetComputer(deviceName);
        if (computer is null)
        {
            return;
        }
        var disabledOuSuffix = ("," + DisabledComputersOuDn).ToLowerInvariant();
        if (computer.DistinguishedName.ToLowerInvariant().EndsWith(disabledOuSuffix))
        {
            return;
        }
        _adService.MoveComputer(deviceName, DisabledComputersOuDn);
        Log($"'{deviceName}' moved into the Disabled Computers OU as part of offboarding.", AppLogLevel.Info);
    }

    /// <summary>The generic "Move to" action's disable-on-exit-from-active-service rule (Francois,
    /// 2026-09-17). Re-enabling a computer only ever happens when a device moves TO Current
    /// Inventory (see ReenableComputerInAdIfNeeded) - every other tab-to-tab move must instead push
    /// the computer toward a disabled state, at one of two strengths the caller selects via
    /// checkFirst:
    ///  - checkFirst: true - lazy. Only acts if AD actually shows the computer enabled right now;
    ///    an already-disabled computer is left completely alone (no redundant AD write, no forced
    ///    re-move if it happens to already be sitting somewhere else). Used for a move where
    ///    NEITHER end is Current Inventory (e.g. IT Stock -&gt; Unknown).
    ///  - checkFirst: false - unconditional, no "already disabled" shortcut. Used specifically when
    ///    a device is leaving Current Inventory (to ANY other tab) - it must always end up disabled
    ///    and in the Disabled Computers OU, mirroring the same unconditional behavior Offboard User
    ///    already applies (see OnOffboardingToolClosed/MoveComputerIntoDisabledOuIfNeeded above).
    /// Best-effort and non-blocking, own try/catch, same treatment as every other automatic AD sync
    /// in this class - a computer not found in AD, or any AD failure, is logged and otherwise
    /// ignored rather than blocking the inventory move that triggered this check.</summary>
    private void DisableComputerAndMoveToDisabledOuIfNeeded(string deviceName, bool checkFirst)
    {
        deviceName = deviceName.Trim();
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            return;
        }
        try
        {
            var computer = _adService.GetComputer(deviceName);
            if (computer is null)
            {
                return;
            }
            if (checkFirst && !computer.Enabled)
            {
                return;
            }
            _adService.SetComputerEnabled(deviceName, false);
            var disabledOuSuffix = ("," + DisabledComputersOuDn).ToLowerInvariant();
            if (!computer.DistinguishedName.ToLowerInvariant().EndsWith(disabledOuSuffix))
            {
                _adService.MoveComputer(deviceName, DisabledComputersOuDn);
            }
            Log($"'{deviceName}' disabled in Active Directory and moved into the Disabled Computers OU.", AppLogLevel.Info);
        }
        catch (Exception ex)
        {
            Log($"Could not disable/relocate '{deviceName}' in Active Directory: {ex.Message}", AppLogLevel.Warning);
        }
    }

    private static void ClearAutoFlagsOnMove(InventoryItem item, string fromStatus, string toStatus)
    {
        if (fromStatus == "DisabledDevices" && toStatus != "DisabledDevices")
        {
            item.ADComputerDisabled = "";
        }
        if (fromStatus == "ComputerNotInAD" && toStatus != "ComputerNotInAD")
        {
            item.ADComputerNotFound = "";
        }
    }

    private void SetIgnoreHighlight(string statusKey, bool ignore)
    {
        var items = GetSelectedItems(statusKey);
        if (items.Count == 0)
        {
            MessageBox.Show(this, "Select one or more items first.", "No Selection", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        foreach (var item in items)
        {
            item.IgnoreHighlight = ignore ? "Yes" : "";
        }
        SaveAndRefresh();
        var names = string.Join(", ", items.Select(i => i.DeviceName));
        Log(ignore
            ? $"Ignore Highlight Color turned ON for {items.Count} item(s): {names}."
            : $"Ignore Highlight Color turned OFF for {items.Count} item(s): {names}.");
    }

    private void InvokeToggleHighlight(string statusKey)
    {
        var items = GetSelectedItems(statusKey);
        if (items.Count == 0)
        {
            MessageBox.Show(this, "Select one or more items first.", "No Selection", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var currentlyIgnored = items[0].IgnoreHighlight == "Yes";
        SetIgnoreHighlight(statusKey, !currentlyIgnored);
    }

    private void SetIgnoreAdSync(string statusKey, bool exclude)
    {
        var items = GetSelectedItems(statusKey);
        if (items.Count == 0)
        {
            MessageBox.Show(this, "Select one or more items first.", "No Selection", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        foreach (var item in items)
        {
            item.IgnoreAdSync = exclude ? "Yes" : "";
        }
        SaveAndRefresh();
        var names = string.Join(", ", items.Select(i => i.DeviceName));
        Log(exclude
            ? $"Excluded {items.Count} item(s) from AD Sync: {names}."
            : $"Re-included {items.Count} item(s) in AD Sync: {names}.");
    }

    private void InvokeToggleAdSync(string statusKey)
    {
        var items = GetSelectedItems(statusKey);
        if (items.Count == 0)
        {
            MessageBox.Show(this, "Select one or more items first.", "No Selection", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var currentlyExcluded = items[0].IgnoreAdSync == "Yes";
        SetIgnoreAdSync(statusKey, !currentlyExcluded);
    }

    /// <summary>Francois, 2026-09-24: "give me a special command where I can mark the item as a
    /// server so it's ignored" - a one-click convenience that both files the record under the new
    /// Servers tab and excludes it from AD Sync (the existing IgnoreAdSync flag, same one "Exclude
    /// from AD Sync" toggles), since a server isn't meant to be classified the same way as a
    /// person's device. Deliberately one-directional, unlike the Highlight/AD-Sync toggles above -
    /// there's no reliable "previous tab" to restore a record to, same as every other Move to
    /// action in this app; reverse it manually via Move to (a different tab) plus Include in AD
    /// Sync if needed.</summary>
    private void MarkSelectedAsServer(string statusKey)
    {
        var items = GetSelectedItems(statusKey);
        if (items.Count == 0)
        {
            MessageBox.Show(this, "Select one or more items first.", "No Selection", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        foreach (var item in items)
        {
            item.Status = "Servers";
            item.IgnoreAdSync = "Yes";
        }
        SaveAndRefresh();
        var names = string.Join(", ", items.Select(i => i.DeviceName));
        Log($"Marked {items.Count} item(s) as Server (moved to Servers, excluded from AD Sync): {names}.");
    }

    /// <summary>Direct manual AD enable/disable for the selected device(s) (Francois, 2026-09-18)
    /// - just flips userAccountControl, nothing else (no OU move, no inventory status/tab change,
    /// unlike the tab-driven auto-enable/disable flows elsewhere in this class). For ad-hoc
    /// troubleshooting - e.g. AD shows a computer disabled for a reason that's already been
    /// resolved, or an admin wants to disable one without moving/offboarding it.</summary>
    private void SetComputerEnabledForSelected(string statusKey, bool enable)
    {
        var items = GetSelectedItems(statusKey);
        if (items.Count == 0)
        {
            MessageBox.Show(this, "Select one or more items first.", "No Selection", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var succeeded = new List<string>();
        var failed = new List<string>();
        foreach (var item in items)
        {
            var deviceName = item.DeviceName.Trim();
            if (string.IsNullOrWhiteSpace(deviceName))
            {
                continue;
            }
            try
            {
                _adService.SetComputerEnabled(deviceName, enable);
                item.ADComputerDisabled = enable ? "" : "Computer object disabled in AD";
                succeeded.Add(deviceName);
            }
            catch (Exception ex)
            {
                failed.Add($"{deviceName}: {ex.Message}");
            }
        }
        SaveAndRefresh();
        if (succeeded.Count > 0)
        {
            Log(enable
                ? $"Enabled {succeeded.Count} computer object(s) in AD: {string.Join(", ", succeeded)}."
                : $"Disabled {succeeded.Count} computer object(s) in AD: {string.Join(", ", succeeded)}.");
        }
        if (failed.Count > 0)
        {
            Log($"Could not {(enable ? "enable" : "disable")} {failed.Count} computer object(s) in AD: {string.Join("; ", failed)}", AppLogLevel.Error);
            MessageBox.Show(this, $"Could not {(enable ? "enable" : "disable")} the following in Active Directory:\r\n\r\n{string.Join("\r\n", failed)}", "Active Directory Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void InvokeToggleComputerEnabled(string statusKey)
    {
        var items = GetSelectedItems(statusKey);
        if (items.Count == 0)
        {
            MessageBox.Show(this, "Select one or more items first.", "No Selection", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var currentlyDisabled = !string.IsNullOrWhiteSpace(items[0].ADComputerDisabled);
        SetComputerEnabledForSelected(statusKey, enable: currentlyDisabled);
    }

    /// <summary>Port of Toggle-DeviceGroup: purely in-memory expand/collapse state, reset on
    /// relaunch, never saved to inventory.json.</summary>
    private void ToggleDeviceGroup(string deviceKey)
    {
        if (string.IsNullOrWhiteSpace(deviceKey))
        {
            return;
        }
        if (!_expandedGroups.Remove(deviceKey))
        {
            _expandedGroups.Add(deviceKey);
        }
        RefreshAllTabs();
    }

    /// <summary>Port of Invoke-AdCheckForSelected: a scoped, on-demand classification of just the
    /// selected device(s) using the same priority rules as the full AD Sync pass, without pulling
    /// every AD user/computer to check the whole inventory.</summary>
    private void InvokeAdCheckForSelected(string statusKey)
    {
        var items = GetSelectedItems(statusKey);
        if (items.Count == 0)
        {
            MessageBox.Show(this, "Select one or more devices to check.", "No Selection", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var checkedCount = 0;
        var movedCount = 0;
        var errorCount = 0;

        foreach (var item in items)
        {
            var deviceName = item.DeviceName.Trim();
            var username = item.Username.Trim();
            var lastName = item.LastName.Trim();
            var firstName = item.FirstName.Trim();
            var isOffDomain = item["Domain"].Trim() == "Off-Domain";

            try
            {
                AdUserInfo? adUser = null;
                var userChecked = false;
                if (!string.IsNullOrWhiteSpace(username))
                {
                    userChecked = true;
                    adUser = _adService.GetUser(username);
                }
                else if (!string.IsNullOrWhiteSpace(lastName))
                {
                    userChecked = true;
                    var candidates = _adService.SearchUsersByExactSurname(lastName);
                    var resolved = AdUserNameResolver.Resolve(candidates, firstName);
                    if (resolved.Outcome == NameMatchOutcome.SingleMatch)
                    {
                        adUser = _adService.GetUser(resolved.Match!.SamAccountName);
                    }
                    else if (resolved.Outcome == NameMatchOutcome.Ambiguous)
                    {
                        using var picker = new AdUserPickerDialog(resolved.Candidates, deviceName);
                        if (picker.ShowDialog(this) == DialogResult.OK && picker.Result is { } picked)
                        {
                            adUser = _adService.GetUser(picked.SamAccountName);
                        }
                    }
                }

                if (userChecked)
                {
                    if (adUser is not null)
                    {
                        item.FirstName = adUser.GivenName;
                        item.LastName = adUser.Surname;
                        item.ADFlagReason = adUser.Enabled ? "" : "AD account disabled";
                    }
                    else
                    {
                        item.ADFlagReason = "No matching AD user found";
                    }
                }
                else
                {
                    item.ADFlagReason = "";
                }

                var noDevice = InventorySchema.IsNoDevice(deviceName);
                var adComputer = noDevice ? null : _adService.GetComputer(deviceName);
                var computerExists = noDevice || adComputer is not null;
                item.ADComputerNotFound = computerExists ? "" : "No matching AD computer object found";

                var disabledOuSuffix = ("," + DisabledComputersOuDn).ToLowerInvariant();
                var computerDisabled = adComputer is not null &&
                    (!adComputer.Enabled || adComputer.DistinguishedName.ToLowerInvariant().EndsWith(disabledOuSuffix, StringComparison.Ordinal));

                if (!isOffDomain && !computerExists && item.Status is not ("ComputerNotInAD" or "DisabledDevices"))
                {
                    item.Status = "ComputerNotInAD";
                    movedCount++;
                }
                if (computerDisabled)
                {
                    item.ADComputerDisabled = "Computer object disabled in AD";
                    if (item.Status != "DisabledDevices")
                    {
                        item.Status = "DisabledDevices";
                        movedCount++;
                    }
                }
                else
                {
                    item.ADComputerDisabled = "";
                }

                var userSummary = !userChecked ? "n/a" : adUser is not null ? "matched" : "not found";
                var compSummary = computerExists ? "found" : "not found";
                Log($"Check AD - '{deviceName}': user {userSummary}, computer {compSummary}, now in '{item.Status}'.");
                checkedCount++;
            }
            catch (Exception ex)
            {
                Log($"Check AD - '{deviceName}': AD lookup failed: {ex.Message}", AppLogLevel.Error);
                errorCount++;
            }
        }

        SaveAndRefresh();
        MessageBox.Show(this,
            $"Checked {checkedCount} device(s) against AD.\r\n{movedCount} moved to a different tab based on the result, {errorCount} errored.\r\n\r\nSee the Activity Log for details.",
            "Check AD Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>Port of Invoke-MatchUserToAd: for each selected device, anchors (or refreshes) its
    /// Username by resolving against AD - already-anchored rows just refresh First/Last Name,
    /// unanchored rows search by exact Last Name with the same ambiguity handling as Check AD.</summary>
    private void InvokeMatchUserToAd(string statusKey)
    {
        var items = GetSelectedItems(statusKey);
        if (items.Count == 0)
        {
            MessageBox.Show(this, "Select one or more devices to match.", "No Selection", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var matchedCount = 0;
        var ambiguousCount = 0;
        var notFoundCount = 0;
        var errorCount = 0;

        foreach (var item in items)
        {
            var deviceName = item.DeviceName.Trim();
            var lastName = item.LastName.Trim();
            var firstName = item.FirstName.Trim();
            AdUserInfo? adUser = null;

            // Always re-search AD by Last Name, even when this device already has a Username -
            // Francois, 2026-09-16: an already-linked device used to just silently re-confirm its
            // existing username (a direct GetUser call, no search at all), which meant Match User
            // did nothing visible for the single most common case people click it for: "did this
            // person's AD account change?" A genuinely correct match to the SAME account it already
            // had is still a real match (matchedCount++, logged) - it simply won't produce a save
            // diff or audit entry if nothing about the linked account's Name actually changed,
            // since InventoryStore only records/mirrors a field that's genuinely different.
            try
            {
                if (string.IsNullOrWhiteSpace(lastName))
                {
                    Log($"Match User - '{deviceName}': no Last Name on record to search AD by.", AppLogLevel.Warning);
                    notFoundCount++;
                    continue;
                }
                var candidates = _adService.SearchUsersByExactSurname(lastName);
                if (candidates.Count == 0)
                {
                    Log($"Match User - '{deviceName}': no AD user found with Last Name '{lastName}'.", AppLogLevel.Warning);
                    notFoundCount++;
                    continue;
                }
                var resolved = AdUserNameResolver.Resolve(candidates, firstName);
                if (resolved.Outcome == NameMatchOutcome.SingleMatch)
                {
                    adUser = _adService.GetUser(resolved.Match!.SamAccountName);
                }
                else
                {
                    using var picker = new AdUserPickerDialog(resolved.Candidates, deviceName);
                    if (picker.ShowDialog(this) == DialogResult.OK && picker.Result is { } picked)
                    {
                        adUser = _adService.GetUser(picked.SamAccountName);
                    }
                    if (adUser is null)
                    {
                        Log($"Match User - '{deviceName}': {candidates.Count} AD users named '{lastName}' found, none selected.", AppLogLevel.Warning);
                        ambiguousCount++;
                        continue;
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Match User - '{deviceName}': AD lookup failed: {ex.Message}", AppLogLevel.Error);
                errorCount++;
                continue;
            }

            if (adUser is null)
            {
                notFoundCount++;
                continue;
            }

            item.Username = adUser.SamAccountName;
            item.FirstName = adUser.GivenName;
            item.LastName = adUser.Surname;
            Log($"Match User - '{deviceName}': matched to {adUser.GivenName} {adUser.Surname} ({adUser.SamAccountName}).");
            matchedCount++;
        }

        SaveAndRefresh();
        MessageBox.Show(this,
            $"Matched {matchedCount} device(s) to AD.\r\n{ambiguousCount} needed manual selection and were skipped, {notFoundCount} had no AD match, {errorCount} errored.\r\n\r\nSee the Activity Log for details.",
            "Match User Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>Port of Write-AppLog: writes the persistent file line (AppLogWriter) and appends
    /// the short display line to the on-screen list, then clears selection after scrolling the
    /// new line into view - selecting only to force the scroll, not to leave it looking picked.
    /// <para><b>2026-09-23:</b> every line is now stamped with this session's own SU username (the
    /// "who" field - see AppLogFormatter's own class comment) and, right after writing, resyncs
    /// TailSharedActivityLog's read position to the file's new length - so the periodic tail never
    /// re-reads and re-appends this session's own line a moment later.</para></summary>
    private void Log(string message, AppLogLevel level = AppLogLevel.Info)
    {
        _appLog.Write(message, level, _suUsername);
        _logListBox.Items.Add(AppLogFormatter.FormatDisplayLine(DateTime.Now, level, message, _suUsername));
        _logListBox.SelectedIndex = _logListBox.Items.Count - 1;
        _logListBox.ClearSelected();
        ResyncAppLogTailPosition();
    }

    private string CurrentAppLogFilePath => Path.Combine(_appLog.LogDirectory, $"InventoryManager_{DateTime.Now:yyyy-MM-dd}.log");

    private void InitializeAppLogTailBaseline()
    {
        _appLogTailDate = DateTime.Now.Date;
        try
        {
            _appLogTailLength = File.Exists(CurrentAppLogFilePath) ? new FileInfo(CurrentAppLogFilePath).Length : 0;
        }
        catch
        {
            _appLogTailLength = 0;
        }
    }

    private void ResyncAppLogTailPosition()
    {
        try
        {
            _appLogTailLength = File.Exists(CurrentAppLogFilePath) ? new FileInfo(CurrentAppLogFilePath).Length : 0;
        }
        catch
        {
            // Best-effort - worst case the next tail poll re-reads (and correctly filters out, by
            // who) this session's own recent lines instead of skipping them outright.
        }
    }

    /// <summary>Picks up other sessions' Activity Log lines from the shared log file - the "why
    /// don't I see other admins' logins" gap Francois raised 2026-09-23: the file itself has been
    /// shared since the INVMGR_DATA_DIR fix, but the on-screen panel only ever displayed this
    /// session's own in-memory Log() calls. Runs on the same 15s timer as the inventory refresh -
    /// "live" here means "within about 15 seconds," matching how every other cross-session signal
    /// in this app already defines it (grid refresh, backup checks). Deliberately does NOT backfill
    /// a brand-new day's file from position 0 when the date rolls over - only lines written from
    /// this point forward are tailed, so launching mid-afternoon into a file three other admins have
    /// already been writing to all day doesn't dump their entire day's history into this session's
    /// panel at once.</summary>
    private void TailSharedActivityLog()
    {
        try
        {
            var today = DateTime.Now.Date;
            if (today != _appLogTailDate)
            {
                _appLogTailDate = today;
                _appLogTailLength = 0;
            }

            var path = CurrentAppLogFilePath;
            if (!File.Exists(path))
            {
                return;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length < _appLogTailLength)
            {
                // Shouldn't happen under normal append-only use - treat as "nothing read yet"
                // rather than throwing on a negative-length seek.
                _appLogTailLength = 0;
            }
            if (stream.Length == _appLogTailLength)
            {
                return;
            }

            stream.Seek(_appLogTailLength, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            var newText = reader.ReadToEnd();
            _appLogTailLength = stream.Length;

            var addedAny = false;
            foreach (var line in newText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
            {
                if (!AppLogFormatter.TryParseFileLine(line, out var ts, out var level, out var who, out var message))
                {
                    continue;
                }
                if (string.Equals(who, _suUsername, StringComparison.OrdinalIgnoreCase))
                {
                    // Our own line - already appended live by Log() itself, skip to avoid a duplicate.
                    continue;
                }
                _logListBox.Items.Add(AppLogFormatter.FormatDisplayLine(ts, level, message, who));
                addedAny = true;
            }
            if (addedAny)
            {
                _logListBox.SelectedIndex = _logListBox.Items.Count - 1;
                _logListBox.ClearSelected();
            }
        }
        catch
        {
            // Best-effort - a locked/unreadable file this poll just means we catch up next poll.
        }
    }

    private Panel BuildLogPanel()
    {
        var logPanel = new Panel { Dock = DockStyle.Right, Width = LogExpandedWidth };

        var btnClearLog = new Button { Text = "Clear Log", Dock = DockStyle.Bottom, Height = 28 };
        btnClearLog.Click += (_, _) => _logListBox.Items.Clear();

        var btnOpenLogFolder = new Button { Text = "Open Logs Folder", Dock = DockStyle.Bottom, Height = 28 };
        btnOpenLogFolder.Click += (_, _) => OpenLogsFolder();

        var logHeaderPanel = new Panel { Dock = DockStyle.Top, Height = 26 };
        var lblLogHeader = new Label
        {
            Text = "Activity Log",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(6, 0, 0, 0),
            Font = new Font(Font, FontStyle.Bold),
        };
        var btnToggleLog = new Button { Text = "▶", Dock = DockStyle.Right, Width = 26, FlatStyle = FlatStyle.Flat };
        // Inner Fill (label) added before the inner Right (button) - same docking-order rule as
        // everywhere else in this form.
        logHeaderPanel.Controls.Add(lblLogHeader);
        logHeaderPanel.Controls.Add(btnToggleLog);

        // Inner Fill (the list) before the inner Bottom buttons, before the inner Top header -
        // same rule, applied one level down.
        logPanel.Controls.Add(_logListBox);
        logPanel.Controls.Add(btnClearLog);
        logPanel.Controls.Add(btnOpenLogFolder);
        logPanel.Controls.Add(logHeaderPanel);

        btnToggleLog.Click += (_, _) =>
        {
            _logCollapsed = !_logCollapsed;
            logPanel.Width = _logCollapsed ? 26 : LogExpandedWidth;
            lblLogHeader.Visible = !_logCollapsed;
            _logListBox.Visible = !_logCollapsed;
            btnClearLog.Visible = !_logCollapsed;
            btnOpenLogFolder.Visible = !_logCollapsed;
            btnToggleLog.Text = _logCollapsed ? "◀" : "▶";
        };

        return logPanel;
    }

    private void OpenLogsFolder()
    {
        var logDir = Path.Combine(Path.GetDirectoryName(_store.DataFilePath) ?? ".", "Logs");
        try
        {
            Directory.CreateDirectory(logDir);
            Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"\"{logDir}\"", UseShellExecute = false });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not open the logs folder: {ex.Message}", "Open Logs Folder", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ImportItemsCsv(string statusKey)
    {
        using var ofd = new OpenFileDialog { Filter = "CSV Files (*.csv)|*.csv|All Files (*.*)|*.*" };
        if (ofd.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }
        try
        {
            var content = File.ReadAllText(ofd.FileName);
            var result = InventoryCsvImporter.Import(content, statusKey, () => InventorySchema.GetNextId(_store.Items));
            _store.Items.AddRange(result.Added);
            if (result.Added.Count > 0)
            {
                SaveAndRefresh();
            }
            var msg = $"Imported {result.Added.Count} item(s).";
            if (result.Skipped > 0)
            {
                msg += $"\r\nSkipped {result.Skipped} completely empty row(s).";
            }
            MessageBox.Show(this, msg, "Import Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
            Log($"Imported {result.Added.Count} item(s) into {statusKey} from '{Path.GetFileName(ofd.FileName)}' ({result.Skipped} empty row(s) skipped).");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Import failed: {ex.Message}", "Import Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Log($"CSV import from '{Path.GetFileName(ofd.FileName)}' failed: {ex.Message}", AppLogLevel.Error);
        }
    }

    private void ExportItemsCsv(string statusKey)
    {
        using var sfd = new SaveFileDialog { Filter = "CSV Files (*.csv)|*.csv", FileName = "inventory_export.csv" };
        if (sfd.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }
        try
        {
            var items = _store.Items.Where(i => i.Status == statusKey).ToList();
            File.WriteAllText(sfd.FileName, InventoryCsvExporter.Export(items));
            MessageBox.Show(this, $"Exported {items.Count} item(s).", "Export Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
            Log($"Exported {items.Count} item(s) from {statusKey} to '{Path.GetFileName(sfd.FileName)}'.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Export failed: {ex.Message}", "Export Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Log($"CSV export to '{Path.GetFileName(sfd.FileName)}' failed: {ex.Message}", AppLogLevel.Error);
        }
    }

    /// <summary>Reloads fresh from disk first (so a lock another session acquired since our last
    /// periodic refresh is never missed), then tries to claim this session's own edit lock on the
    /// given item - see RecordEditLock. Returns the freshly-loaded InventoryItem instance to edit
    /// on success; shows why and returns null if the record is gone or someone else holds it.</summary>
    private InventoryItem? TryAcquireEditLock(int itemId, string actionVerb)
    {
        _store.Load();
        var item = _store.Items.FirstOrDefault(i => i.Id == itemId);
        if (item is null)
        {
            MessageBox.Show(this, "This record no longer exists - it may have been deleted by another session.", "Not Found", MessageBoxButtons.OK, MessageBoxIcon.Information);
            RefreshAllTabs();
            return null;
        }
        var utcNow = DateTime.UtcNow;
        if (RecordEditLock.IsLockedByOther(item, _suUsername, utcNow))
        {
            var lockedSince = DateTime.TryParse(item.EditLockedAtUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var lockedAtUtc)
                ? lockedAtUtc.ToLocalTime().ToString("t")
                : "an unknown time";
            MessageBox.Show(this, $"'{item.DeviceName}' is currently being {actionVerb} by '{item.EditLockedBy}' (since {lockedSince}). Try again in a few minutes.", "Record Locked", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            RefreshAllTabs();
            return null;
        }
        RecordEditLock.Acquire(item, _suUsername, utcNow);
        SaveAndRefresh();
        return _store.Items.FirstOrDefault(i => i.Id == itemId);
    }

    private void SaveAndRefresh()
    {
        var result = _store.Save();
        if (result == InventorySaveResult.LockTimedOut)
        {
            MessageBox.Show(this, "Could not save - the inventory file has been locked by another user for too long. Your changes are still here; try again in a moment.", "Save Locked", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        RecordFieldChangeAudits(_store.LastSaveFieldChanges);
        _lastKnownFileWriteTimeUtc = File.GetLastWriteTimeUtc(_store.DataFilePath);
        RefreshAllTabs();
    }

    /// <summary>The admin accounts this app must not touch - see ProtectedAccountPolicy. Cached for
    /// a few minutes so a bulk save (one check per changed field) costs one AD round-trip, not one
    /// per row. An AD failure is never cached and fails closed: every account counts as protected
    /// until the list can actually be loaded.</summary>
    private ProtectedAccountPolicy GetProtectedAccountPolicy()
    {
        if (_protectedAccounts is not null && DateTime.UtcNow - _protectedAccountsLoadedUtc < ProtectedAccountCacheLifetime)
        {
            return _protectedAccounts;
        }
        try
        {
            _protectedAccounts = ProtectedAccountPolicy.Load(_adService, _adService.GetDomainSid());
            _protectedAccountsLoadedUtc = DateTime.UtcNow;
            return _protectedAccounts;
        }
        catch (Exception ex)
        {
            Log($"Could not load the protected (admin) account list from AD - treating every account as protected until it can: {ex.Message}", AppLogLevel.Warning);
            return ProtectedAccountPolicy.FailClosed("could not confirm with AD that it isn't an admin account");
        }
    }

    /// <summary>Entry gate for the actions that deliberately write to a user account (Transfer,
    /// Offboard, Copy Permissions target). Returns true - after telling the operator, logging and
    /// auditing - when the account is protected and the action must not run.</summary>
    private bool RefuseIfProtectedAccount(string username, string actionTitle)
    {
        var reason = GetProtectedAccountPolicy().GetProtectionReason(username);
        if (reason is null)
        {
            return false;
        }
        _audit.Record(SecurityEventType.ProtectedAccountRefused, SecuritySeverity.Warning, _suUsername, username,
            $"{actionTitle} refused for protected account '{username}' ({reason}). Nothing changed.");
        Log($"{actionTitle} refused for '{username}' - protected admin account ({reason}).", AppLogLevel.Warning);
        MessageBox.Show(this,
            $"'{username}' is a protected administrative account ({reason}).\r\n\r\nInventory Manager doesn't make changes to admin accounts - manage it directly in Active Directory Users and Computers. Nothing was changed.",
            actionTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return true;
    }

    /// <summary>Appends an AD Notes line as a side effect of an inventory change, unless the account
    /// is protected - in which case only the local ledger (already written by the caller) keeps
    /// the record. Best-effort like every Notes mirror: a failure is logged, never thrown.</summary>
    private void AppendSideEffectNote(string username, string noteText, string context)
    {
        if (GetProtectedAccountPolicy().GetProtectionReason(username) is { } reason)
        {
            Log($"Skipped AD Notes for '{username}' ({context}) - protected admin account ({reason}).");
            return;
        }
        try
        {
            AdNoteAppender.Append(_adService, username, noteText);
        }
        catch (Exception ex)
        {
            // Best-effort mirror only - an AD write failure (offline DC, no rights to this one
            // account, stale/renamed AD identity, etc.) must never block or roll back a save that
            // already succeeded locally, and the local ledger already has the real record. But it
            // must not be SILENT either - Francois, 2026-09-23: "I just made a change to a user's
            // record but the phone's notes section is not being updated" was completely invisible
            // before failures here were logged.
            Log($"Could not update AD Notes for '{username}' ({context}): {ex.Message}", AppLogLevel.Warning);
        }
    }

    /// <summary>Meticulous per-field audit trail, requested 2026-09-15: every SaveAndRefresh runs
    /// through here regardless of which action triggered it (Edit Item, Move to, Clear Username,
    /// Check AD, Match User, CSV import, Ignore Highlight, ...), so nothing that changes a real
    /// field can skip being logged - one entry per changed field, never one combined entry per
    /// save, matching exactly what Francois asked to see. Two destinations per change: an
    /// unbounded, tamper-evident local ledger (the actual "when I get audited" source of truth -
    /// nothing here ever gets dropped), and a short mirror into the linked AD user's own Notes
    /// field (capped at 1000 chars, so oldest entries eventually roll off - at-a-glance only, not
    /// the record of record). A device with no linked Username still gets the local entry; there's
    /// simply no AD account to also write the second copy to.</summary>
    private void RecordFieldChangeAudits(IReadOnlyList<InventoryFieldChange> changes)
    {
        foreach (var change in changes)
        {
            var subject = !string.IsNullOrWhiteSpace(change.Username) ? change.Username : change.DeviceName;
            var actionText = $"{subject}'s inventory record was modified/updated. {change.FieldLabel} was changed by {_suUsername}";
            var timestamp = DateTime.Now.ToString("M/d/yyyy h:mm tt");

            // Target is the DEVICE, not "subject" (which drifts between the linked Username and the
            // device name depending on assignment state at the time of each change) - Francois asked
            // (2026-09-16) to be able to "pull the log" for one specific record on demand, unbounded
            // by AD Notes' ~1000-char cap. DeviceName is the one thing that never changes across a
            // device's whole history (reassignments change Username, not DeviceName), so keying every
            // entry on it here is what makes ShowDeviceChangeHistory's filter reliable regardless of
            // how many times this device has changed hands.
            _inventoryAudit.Record(SecurityEventType.InventoryFieldChanged, SecuritySeverity.Info, _suUsername, change.DeviceName,
                $"[{timestamp}] {actionText} (from '{change.OldValue}' to '{change.NewValue}').");

            if (change.FieldKey == "Username")
            {
                // A device changing hands is the one field change where the generic "Username was
                // changed by SU" mirror is actively confusing to read in ADUC - Francois asked
                // (2026-09-16) for each affected person's OWN account to get a note in their own
                // terms instead: the person losing the device is told it was removed, the person
                // gaining it is told it was added. Fires for every path that changes Username, not
                // just the IT Stock/Disabled Devices -> Current promotion flow - Transfer, Match
                // User, and Clear Username all go through this same save pipeline.
                RecordDeviceReassignmentNotes(change);
            }
            else if (!string.IsNullOrWhiteSpace(change.Username))
            {
                AppendSideEffectNote(change.Username, actionText, $"{change.FieldLabel} change");
            }

            // Francois, 2026-09-16: editing Description here never used to reach the AD computer
            // object at all - InventorySchema's Description field has carried a SyncToAd flag
            // since Phase 0/1 that nothing ever actually acted on until now. Push it automatically
            // on every save that changes it, same best-effort/non-blocking treatment as the AD
            // Notes mirror above. (The manual Pull/Push Description commands were removed
            // 2026-10-14; a failed push is logged as a warning below, and re-saving the
            // Description retries it.)
            if (change.FieldKey == "Description" && !InventorySchema.IsNoDevice(change.DeviceName))
            {
                try
                {
                    _adService.SetComputerDescription(change.DeviceName, change.NewValue);
                }
                catch (Exception ex)
                {
                    Log($"Could not push Description to AD for '{change.DeviceName}': {ex.Message}", AppLogLevel.Warning);
                }
            }
        }
    }

    /// <summary>Writes each affected person's own AD Notes a plain-language line about this one
    /// device changing hands - the person losing it (change.OldValue, when non-blank) is told it
    /// was removed, the person gaining it (change.NewValue, when non-blank) is told it was added.
    /// Either side can be blank (Clear Username only has an old side; Match User on a
    /// previously-unlinked device only has a new side) - each note is independent and best-effort,
    /// matching every other AD-Notes write in this method.</summary>
    private void RecordDeviceReassignmentNotes(InventoryFieldChange change)
    {
        if (!string.IsNullOrWhiteSpace(change.OldValue))
        {
            AppendSideEffectNote(change.OldValue, $"{change.DeviceName} was removed from your assigned inventory by {_suUsername}", "device removed");
        }
        if (!string.IsNullOrWhiteSpace(change.NewValue))
        {
            AppendSideEffectNote(change.NewValue, $"{change.DeviceName} was added to your assigned inventory by {_suUsername}", "device added");
        }
    }
}
