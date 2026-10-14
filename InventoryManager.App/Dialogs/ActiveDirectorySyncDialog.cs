using InventoryManager.Core;
using InventoryManager.Core.Ad;
using InventoryManager.Core.Logging;
using InventoryManager.Core.Models;
using InventoryManager.Core.Persistence;

namespace InventoryManager.App.Dialogs;

/// <summary>
/// Port of the PowerShell build's Show-ActiveDirectorySyncDialog: runs the full
/// AdUserMatchCheckService pass across the whole inventory (the same engine "Check AD" runs
/// per-row) and shows the results as a 5-tab dashboard - User Disabled, User Not Found in AD,
/// Computer Disabled, Recent AD Changes (30 days), and Computers Not in Inventory (with an import
/// action). Running the check is a real write (it saves the inventory with refreshed AD flags/
/// auto-moved statuses), same as the PowerShell build.
/// </summary>
public sealed class ActiveDirectorySyncDialog : Form
{
    private const string ComputersOuDn = "OU=Domain Computers,DC=contoso,DC=local";
    private const string DisabledComputersOuDn = "OU=Disabled Computers,OU=Domain Computers,DC=contoso,DC=local";

    private readonly IAdService _adService;
    private readonly InventoryStore _store;
    private readonly Action _saveAndRefresh;
    private readonly Action<string, AppLogLevel> _log;

    private readonly DataGridView _gridDisabled = NewReadOnlyGrid();
    private readonly DataGridView _gridNotFound = NewReadOnlyGrid();
    private readonly DataGridView _gridCompDisabled = NewReadOnlyGrid();
    private readonly DataGridView _gridChanges = NewReadOnlyGrid();
    private readonly DataGridView _gridMissing = NewReadOnlyGrid(multiSelect: true);
    private readonly ComboBox _cmbImportTarget = new() { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(125, 7), Size = new Size(160, 20) };
    private readonly Label _lblResult = new() { Text = "Running Active Directory check...", Location = new Point(10, 8), Size = new Size(680, 34), AutoEllipsis = true };

    private static DataGridView NewReadOnlyGrid(bool multiSelect = false) => new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        MultiSelect = multiSelect,
        SelectionMode = multiSelect ? DataGridViewSelectionMode.FullRowSelect : DataGridViewSelectionMode.CellSelect,
    };

    public ActiveDirectorySyncDialog(IAdService adService, InventoryStore store, Action saveAndRefresh, Action<string, AppLogLevel> log)
    {
        _adService = adService;
        _store = store;
        _saveAndRefresh = saveAndRefresh;
        _log = log;

        Icon = AppIcon.Shared;
        Text = "Active Directory Sync";
        Size = new Size(900, 600);
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(700, 400);

        var tabs = new TabControl { Dock = DockStyle.Fill };

        var tabDisabled = new TabPage("User Disabled");
        tabDisabled.Controls.Add(_gridDisabled);
        var tabNotFound = new TabPage("User Not Found in AD");
        tabNotFound.Controls.Add(_gridNotFound);
        var tabCompDisabled = new TabPage("Computer Disabled");
        tabCompDisabled.Controls.Add(_gridCompDisabled);
        var tabChanges = new TabPage("Recent AD Changes (30 days)");
        tabChanges.Controls.Add(_gridChanges);

        var tabMissing = new TabPage("Computers Not in Inventory");
        var importPanel = new Panel { Dock = DockStyle.Top, Height = 36 };
        importPanel.Controls.Add(new Label { Text = "Import selected as:", Location = new Point(10, 10), Size = new Size(110, 20) });
        foreach (var status in InventorySchema.Statuses)
        {
            _cmbImportTarget.Items.Add(status.Label);
        }
        var itStockIndex = _cmbImportTarget.Items.IndexOf("IT Stock");
        _cmbImportTarget.SelectedIndex = itStockIndex >= 0 ? itStockIndex : 0;
        importPanel.Controls.Add(_cmbImportTarget);
        var btnImportMissing = new Button { Text = "Import Selected", Location = new Point(295, 5), Size = new Size(120, 26) };
        btnImportMissing.Click += (_, _) => ImportSelectedMissing();
        importPanel.Controls.Add(btnImportMissing);
        tabMissing.Controls.Add(_gridMissing);
        tabMissing.Controls.Add(importPanel);

        tabs.TabPages.Add(tabDisabled);
        tabs.TabPages.Add(tabNotFound);
        tabs.TabPages.Add(tabCompDisabled);
        tabs.TabPages.Add(tabChanges);
        tabs.TabPages.Add(tabMissing);

        var bottomPanel = new Panel { Dock = DockStyle.Bottom, Height = 45 };
        bottomPanel.Controls.Add(_lblResult);
        var btnRefresh = new Button { Text = "Refresh", Location = new Point(700, 8), Size = new Size(80, 28) };
        btnRefresh.Click += (_, _) => RunCheck();
        bottomPanel.Controls.Add(btnRefresh);
        var btnClose = new Button { Text = "Close", Location = new Point(790, 8), Size = new Size(80, 28) };
        btnClose.Click += (_, _) => Close();
        bottomPanel.Controls.Add(btnClose);

        Controls.Add(tabs);
        Controls.Add(bottomPanel);

        Shown += (_, _) => RunCheck();
    }

    private void RunCheck()
    {
        _lblResult.Text = "Running Active Directory check... (this can take several seconds - the window may look unresponsive, it isn't)";
        Cursor = Cursors.WaitCursor;
        Application.DoEvents();

        try
        {
            AdMatchCheckResult? matchResult;
            try
            {
                var allUsers = _adService.ListAllUsers();
                var allComputers = _adService.ListComputersInOu(ComputersOuDn);
                matchResult = AdUserMatchCheckService.Run(_store.Items, allUsers, allComputers, DisabledComputersOuDn);
                _saveAndRefresh();

                // Francois, 2026-09-17: "log everything that was changed/moved with the AD sync" -
                // one Activity Log line per device this run actually changed something about
                // (never a line for every row checked - most runs touch nothing), plus one overall
                // summary line. The per-field tamper-evident ledger already separately records
                // Status/FirstName/LastName changes via the normal SaveAndRefresh path - this is
                // purely for at-a-glance visibility in the Activity Log.
                _log($"Active Directory Sync: checked {matchResult.TotalAdUsers} AD user(s) - {matchResult.Changes.Count} record(s) changed ({matchResult.AutoMovedComputerNotInAdCount} moved to Computer Not in AD, {matchResult.AutoMovedCount} moved to Disabled Devices).", AppLogLevel.Info);
                foreach (var change in matchResult.Changes)
                {
                    _log($"AD Sync - '{change.DeviceName}': {change.Summary}", AppLogLevel.Info);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Could not query Active Directory: {ex.Message}", "Active Directory Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                matchResult = null;
            }

            if (matchResult is not null)
            {
                _gridDisabled.DataSource = _store.Items.Where(i => i.ADFlagReason == "AD account disabled")
                    .Select(i => new { i.DeviceName, i.LastName, i.FirstName, Status = i.Status }).ToList();
                _gridNotFound.DataSource = _store.Items.Where(i => i.ADFlagReason == "No matching AD user found")
                    .Select(i => new { i.DeviceName, i.LastName, i.FirstName, Status = i.Status }).ToList();
                _gridCompDisabled.DataSource = _store.Items.Where(i => !string.IsNullOrEmpty(i.ADComputerDisabled))
                    .Select(i => new { i.DeviceName, i.LastName, i.FirstName, Status = i.Status }).ToList();
            }

            IReadOnlyList<AdUserChangeInfo> changes = [];
            IReadOnlyList<string> missingComputers = [];
            try
            {
                changes = _adService.GetRecentlyChangedUsers(30);
                _gridChanges.DataSource = changes.Select(c => new
                {
                    Name = $"{c.GivenName} {c.Surname}",
                    c.Enabled,
                    c.WhenChanged,
                    c.WhenCreated,
                    MatchedCount = InventoryLookup.FindByName(_store.Items, c.Surname, c.GivenName).Count,
                    MatchText = FormatMatchText(c),
                }).ToList();

                var enabledComputerNames = _adService.ListComputersInOu(ComputersOuDn).Where(c => c.Enabled).Select(c => c.Name);
                missingComputers = InventoryLookup.FindComputersNotInInventory(_store.Items, enabledComputerNames);
                _gridMissing.DataSource = missingComputers.Select(name => new { ComputerName = name }).ToList();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Could not query Active Directory: {ex.Message}", "Active Directory Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            _lblResult.Text = matchResult is not null
                ? $"Checked against {matchResult.TotalAdUsers} AD user(s): {matchResult.ComputerNotFoundCount} computer(s) not in AD ({matchResult.AutoMovedComputerNotInAdCount} auto-moved to Computer Not in AD), {matchResult.NotFoundDeviceCount} have no matching AD user (see the tab above), {matchResult.DisabledDeviceCount} belong to a disabled account, {matchResult.ComputerDisabledCount} have a disabled computer object ({matchResult.AutoMovedCount} auto-moved to Disabled Devices). {changes.Count} recent AD change(s). {missingComputers.Count} AD computer(s) not in inventory."
                : "Active Directory check could not complete - see error dialog.";
        }
        finally
        {
            Cursor = Cursors.Default;
        }
    }

    private string FormatMatchText(AdUserChangeInfo change)
    {
        var matches = InventoryLookup.FindByName(_store.Items, change.Surname, change.GivenName);
        if (matches.Count == 0)
        {
            return "No matching device";
        }
        return string.Join("; ", matches.Select(m =>
        {
            var label = InventorySchema.Statuses.FirstOrDefault(s => s.Key == m.Status)?.Label ?? m.Status;
            return $"{m.DeviceName} ({label})";
        }));
    }

    private void ImportSelectedMissing()
    {
        if (_gridMissing.SelectedRows.Count == 0)
        {
            MessageBox.Show(this, "Select one or more computers to import.", "No Selection", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var targetLabel = (string)_cmbImportTarget.SelectedItem!;
        var targetStatus = InventorySchema.Statuses.First(s => s.Label == targetLabel).Key;
        var names = _gridMissing.SelectedRows.Cast<DataGridViewRow>()
            .Select(r => (string)((dynamic)r.DataBoundItem!).ComputerName)
            .ToList();
        var confirm = MessageBox.Show(this, $"Import {names.Count} computer(s) into '{targetLabel}'?", "Confirm Import", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (confirm != DialogResult.Yes)
        {
            return;
        }

        var added = 0;
        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }
            var item = new InventoryItem { Id = InventorySchema.GetNextId(_store.Items), Status = targetStatus };
            foreach (var field in InventorySchema.FieldDefs)
            {
                item[field.Key] = InventorySchema.GetFieldDefault(field);
            }
            item.DeviceName = name.Trim();
            item["Domain"] = "CONTOSO";
            _store.Items.Add(item);
            added++;
        }
        if (added > 0)
        {
            _saveAndRefresh();
        }
        MessageBox.Show(this, $"Imported {added} computer(s) into '{targetLabel}'.", "Import Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
        RunCheck();
    }
}
