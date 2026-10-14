using InventoryManager.App.Onboarding;
using InventoryManager.Core.Ad;
using InventoryManager.Core.Logging;
using InventoryManager.Core.Permissions;
using InventoryManager.Core.Security;

namespace InventoryManager.App.Permissions;

/// <summary>
/// Copy User Permissions: pick a source and a target user, Scan to see what the source has that
/// the target doesn't, tick what to copy, Apply. Add-only - nothing is ever removed from the
/// target. Scan/Apply run off the UI thread (a full scan across AD + M365 + shares can take a
/// while) with a Cancel button; every Apply is logged, audited, noted on the target's AD account,
/// and saved as a CSV under Logs\PermissionCopies so it can be undone by hand.
/// </summary>
public sealed class CopyPermissionsDialog : Form
{
    private readonly IAdService _adService;
    private readonly string _suUsername;
    private readonly ISecurityAudit _audit;
    private readonly Action<string, AppLogLevel> _log;
    private readonly string _csvDirectory;
    private readonly Func<string, bool>? _isProtectedTarget;

    private readonly TextBox _txtSource;
    private readonly TextBox _txtTarget;
    private readonly CheckBox _chkAdGroups;
    private readonly Button _btnScan;
    private readonly DataGridView _grid;
    private readonly Label _lblStatus;
    private readonly Button _btnSelectAll;
    private readonly Button _btnSelectNone;
    private readonly Button _btnApply;
    private readonly Button _btnExport;
    private readonly Button _btnCancelOp;
    private readonly Button _btnPickSource;
    private readonly Button _btnPickTarget;

    private AdUserInfo? _source;
    private AdUserInfo? _target;
    private PermissionCopyContext? _scannedContext;
    private PermissionCopyService? _service;
    private PermissionCopyReport? _lastReport;
    private CancellationTokenSource? _cts;

    private const string ColApply = "Apply";
    private const string ColResult = "Result";

    public CopyPermissionsDialog(IAdService adService, string suUsername, ISecurityAudit audit,
        Action<string, AppLogLevel> log, string csvDirectory, AdUserInfo? initialSource = null,
        Func<string, bool>? isProtectedTarget = null)
    {
        _adService = adService;
        _suUsername = suUsername;
        _audit = audit;
        _log = log;
        _csvDirectory = csvDirectory;
        // Called when a target is picked; returns true (having already told the operator why)
        // for a protected admin account, which is then never accepted as a target. The source is
        // only read, so it isn't checked.
        _isProtectedTarget = isProtectedTarget;

        Icon = AppIcon.Shared;
        Text = "Copy User Permissions";
        Size = new Size(1050, 680);
        MinimumSize = new Size(800, 500);
        StartPosition = FormStartPosition.CenterParent;

        var top = new Panel { Dock = DockStyle.Top, Height = 118 };
        top.Controls.Add(new Label { Text = "Copy FROM:", Location = new Point(10, 14), Size = new Size(80, 20) });
        _txtSource = new TextBox { Location = new Point(95, 11), Size = new Size(420, 20), ReadOnly = true };
        _btnPickSource = new Button { Text = "Pick...", Location = new Point(525, 9), Size = new Size(80, 26) };
        top.Controls.Add(new Label { Text = "Copy TO:", Location = new Point(10, 46), Size = new Size(80, 20) });
        _txtTarget = new TextBox { Location = new Point(95, 43), Size = new Size(420, 20), ReadOnly = true };
        _btnPickTarget = new Button { Text = "Pick...", Location = new Point(525, 41), Size = new Size(80, 26) };
        var btnSwap = new Button { Text = "Swap", Location = new Point(615, 25), Size = new Size(60, 26) };

        top.Controls.Add(new Label { Text = "Include:", Location = new Point(10, 82), Size = new Size(80, 20) });
        _chkAdGroups = new CheckBox { Text = "AD groups", Location = new Point(95, 80), Size = new Size(110, 22), Checked = true };
        _btnScan = new Button { Text = "Scan", Location = new Point(700, 9), Size = new Size(100, 58) };
        top.Controls.AddRange([_txtSource, _btnPickSource, _txtTarget, _btnPickTarget, btnSwap, _chkAdGroups, _btnScan]);
        top.Controls.Add(new Label
        {
            Text = "Adds only - nothing is removed from the target.",
            Location = new Point(820, 14), Size = new Size(200, 50), ForeColor = SystemColors.GrayText,
        });

        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            EditMode = DataGridViewEditMode.EditOnEnter,
        };
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = ColApply, HeaderText = "Copy", FillWeight = 8 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Category", HeaderText = "Category", ReadOnly = true, FillWeight = 18 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Item", HeaderText = "Item", ReadOnly = true, FillWeight = 34 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Detail", HeaderText = "Detail", ReadOnly = true, FillWeight = 22 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Status", HeaderText = "Status", ReadOnly = true, FillWeight = 40 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = ColResult, HeaderText = "Result", ReadOnly = true, FillWeight = 30 });
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty)
            {
                _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        };
        _grid.CellValueChanged += (_, _) => UpdateButtons();

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 48 };
        _lblStatus = new Label { Text = "Pick both users, then Scan.", Location = new Point(10, 16), Size = new Size(420, 20), AutoEllipsis = true };
        _btnCancelOp = new Button { Text = "Stop", Location = new Point(435, 10), Size = new Size(60, 28), Enabled = false };
        _btnSelectAll = new Button { Text = "Select All", Location = new Point(505, 10), Size = new Size(85, 28) };
        _btnSelectNone = new Button { Text = "Select None", Location = new Point(595, 10), Size = new Size(85, 28) };
        _btnApply = new Button { Text = "Apply Selected", Location = new Point(695, 10), Size = new Size(110, 28), Enabled = false };
        _btnExport = new Button { Text = "Export CSV...", Location = new Point(815, 10), Size = new Size(95, 28), Enabled = false };
        var btnClose = new Button { Text = "Close", Location = new Point(920, 10), Size = new Size(95, 28), DialogResult = DialogResult.Cancel };
        bottom.Controls.AddRange([_lblStatus, _btnCancelOp, _btnSelectAll, _btnSelectNone, _btnApply, _btnExport, btnClose]);

        Controls.Add(_grid);
        Controls.Add(bottom);
        Controls.Add(top);
        CancelButton = btnClose;

        _btnPickSource.Click += (_, _) => PickUser(isSource: true);
        _btnPickTarget.Click += (_, _) => PickUser(isSource: false);
        btnSwap.Click += (_, _) =>
        {
            (_source, _target) = (_target, _source);
            ShowUsers();
            ClearResults();
        };
        _btnScan.Click += async (_, _) => await ScanAsync();
        _btnApply.Click += async (_, _) => await ApplyAsync();
        _btnExport.Click += (_, _) => ExportCsv();
        _btnCancelOp.Click += (_, _) =>
        {
            _cts?.Cancel();
            _lblStatus.Text = "Stopping after the current item...";
        };
        _btnSelectAll.Click += (_, _) => SetAllChecked(true);
        _btnSelectNone.Click += (_, _) => SetAllChecked(false);
        FormClosing += (_, e) =>
        {
            if (_cts is not null)
            {
                // Never abandon an Apply half-way with no record - stop cleanly first.
                e.Cancel = true;
                _cts.Cancel();
                MessageBox.Show(this, "An operation is still running. It will stop after the current item; close again once it finishes.",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        };

        _source = initialSource;
        ShowUsers();
        UpdateButtons();
    }

    private void PickUser(bool isSource)
    {
        using var picker = new ManagerPickerDialog(_adService, isSource ? "Copy permissions FROM" : "Copy permissions TO");
        if (picker.ShowDialog(this) != DialogResult.OK || picker.Selected is not { } picked)
        {
            return;
        }
        AdUserInfo? user;
        try
        {
            user = _adService.GetUser(picked.SamAccountName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not read '{picked.SamAccountName}' from Active Directory: {ex.Message}", "Active Directory Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        if (user is null)
        {
            MessageBox.Show(this, $"Could not find '{picked.SamAccountName}' in Active Directory.", "Active Directory Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        if (isSource)
        {
            _source = user;
        }
        else
        {
            if (_isProtectedTarget?.Invoke(user.SamAccountName) == true)
            {
                return;
            }
            _target = user;
        }
        ShowUsers();
        ClearResults();
    }

    private void ShowUsers()
    {
        _txtSource.Text = Describe(_source);
        _txtTarget.Text = Describe(_target);
        UpdateButtons();
    }

    private static string Describe(AdUserInfo? user) => user is null
        ? ""
        : $"{user.GivenName} {user.Surname} ({user.SamAccountName}){(user.Enabled ? "" : "  - DISABLED")}".Trim();

    private List<IPermissionProvider> BuildProviders()
    {
        var providers = new List<IPermissionProvider>();
        if (_chkAdGroups.Checked)
        {
            providers.Add(new AdGroupPermissionProvider(_adService, audit: _audit));
        }
        return providers;
    }

    private async Task ScanAsync()
    {
        if (_source is null || _target is null)
        {
            MessageBox.Show(this, "Pick both a source and a target user first.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var providers = BuildProviders();
        if (providers.Count == 0)
        {
            MessageBox.Show(this, "Tick at least one thing to include.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ClearResults();
        var context = new PermissionCopyContext(_source, _target, _suUsername);
        var service = new PermissionCopyService(_adService, providers, _audit);
        PermissionScanResult? scan = null;
        await RunBusyAsync("Scanning...", ct => scan = service.Scan(context, MakeProgress(), ct));
        if (scan is null)
        {
            return;
        }

        _service = service;
        _scannedContext = context;
        foreach (var item in scan.Items.OrderBy(i => i.Category).ThenBy(i => i.Status).ThenBy(i => i.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            AddRow(item);
        }
        var toAdd = scan.Items.Count(i => i.CanApply);
        _lblStatus.Text = $"{scan.Items.Count} found on source - {toAdd} can be copied, {scan.Items.Count(i => i.Status == PermissionItemStatus.AlreadyHas)} already on target.";
        _log($"Copy User Permissions: scanned {context.Source.SamAccountName} -> {context.Target.SamAccountName} ({toAdd} copyable).", AppLogLevel.Info);
        if (scan.Errors.Count > 0)
        {
            var errors = string.Join("\r\n", scan.Errors.Select(e => $"- {e.ProviderName}: {e.Message}"));
            _log($"Copy User Permissions scan errors: {errors.Replace("\r\n", " ")}", AppLogLevel.Warning);
            MessageBox.Show(this, $"Some sections couldn't be scanned; the rest are shown.\r\n\r\n{errors}", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        UpdateButtons();
    }

    private void AddRow(PermissionItem item)
    {
        var statusText = item.Status switch
        {
            PermissionItemStatus.ToAdd => "Will be added",
            PermissionItemStatus.AlreadyHas => "Target already has this",
            PermissionItemStatus.Blocked => $"Blocked: {item.StatusReason}",
            PermissionItemStatus.HandledElsewhere => item.StatusReason,
            _ => item.StatusReason,
        };
        var index = _grid.Rows.Add(item.CanApply && item.DefaultChecked, PermissionCopyService.CategoryLabel(item.Category),
            item.DisplayName, item.Detail, statusText, "");
        var row = _grid.Rows[index];
        row.Tag = item;
        if (!item.CanApply)
        {
            row.Cells[ColApply].ReadOnly = true;
            row.DefaultCellStyle.ForeColor = SystemColors.GrayText;
        }
        if (item.Status == PermissionItemStatus.Blocked)
        {
            row.DefaultCellStyle.BackColor = Color.MistyRose;
        }
        row.Cells["Status"].ToolTipText = item.StatusReason;
    }

    private async Task ApplyAsync()
    {
        if (_service is null || _scannedContext is null)
        {
            return;
        }
        var selected = CheckedRows().Select(r => (PermissionItem)r.Tag!).ToList();
        if (selected.Count == 0)
        {
            return;
        }
        var ctx = _scannedContext;
        var lines = selected.Take(25).Select(i => $"  - {PermissionCopyService.CategoryLabel(i.Category)}: {i.DisplayName}").ToList();
        if (selected.Count > 25)
        {
            lines.Add($"  ...and {selected.Count - 25} more");
        }
        var confirm = MessageBox.Show(this,
            $"Copy {selected.Count} permission(s)\r\nFROM {Describe(ctx.Source)}\r\nTO {Describe(ctx.Target)}?\r\n\r\n{string.Join("\r\n", lines)}",
            Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        if (confirm != DialogResult.Yes)
        {
            return;
        }

        _log($"Copy User Permissions: applying {selected.Count} item(s) {ctx.Source.SamAccountName} -> {ctx.Target.SamAccountName}.", AppLogLevel.Info);
        PermissionCopyReport? report = null;
        await RunBusyAsync("Applying...", ct => report = _service.Apply(ctx, selected, MakeProgress(), ct));
        if (report is null)
        {
            return;
        }
        _lastReport = report;

        var byItem = report.Results.GroupBy(r => r.Item).ToDictionary(g => g.Key, g => g.Last());
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.Tag is PermissionItem item && byItem.TryGetValue(item, out var result))
            {
                row.Cells[ColResult].Value = result.Message;
                row.Cells[ColResult].Style.ForeColor = result.Success ? Color.DarkGreen : Color.DarkRed;
                row.Cells[ColApply].Value = false;
                row.Cells[ColApply].ReadOnly = true;
            }
        }

        var csvPath = SaveReportCsv(report);
        var summary = $"{report.Succeeded} succeeded, {report.Failed} failed{(report.Cancelled ? " (stopped early)" : "")}.";
        _lblStatus.Text = summary;
        _log($"Copy User Permissions {ctx.Source.SamAccountName} -> {ctx.Target.SamAccountName}: {summary}{(csvPath is null ? "" : $" Record: {csvPath}")}",
            report.Failed > 0 ? AppLogLevel.Warning : AppLogLevel.Info);
        foreach (var failure in report.Results.Where(r => !r.Success))
        {
            _log($"  Not copied - {failure.Item.DisplayName}: {failure.Message}", AppLogLevel.Warning);
        }
        MessageBox.Show(this,
            $"{summary}\r\n\r\n{(csvPath is null ? "The record CSV could not be saved automatically - use Export CSV." : $"A record of what was applied was saved to:\r\n{csvPath}")}" +
            "\r\n\r\nAD group changes reach Microsoft 365 after the next directory sync.",
            Text, MessageBoxButtons.OK, report.Failed > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        UpdateButtons();
    }

    /// <summary>Runs <paramref name="work"/> on a worker thread with the UI locked except Stop.</summary>
    private async Task RunBusyAsync(string label, Action<CancellationToken> work)
    {
        _cts = new CancellationTokenSource();
        SetBusy(true);
        _lblStatus.Text = label;
        try
        {
            var token = _cts.Token;
            await Task.Run(() => work(token));
        }
        catch (OperationCanceledException)
        {
            _lblStatus.Text = "Stopped.";
        }
        catch (PermissionCopyBlockedException ex)
        {
            _lblStatus.Text = "Blocked.";
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            _lblStatus.Text = "Failed.";
            _log($"Copy User Permissions failed: {ex.Message}", AppLogLevel.Error);
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            SetBusy(false);
        }
    }

    /// <summary>Created on the UI thread, so Report() marshals back to it automatically.</summary>
    private Progress<string> MakeProgress() => new(message => _lblStatus.Text = message);

    private void SetBusy(bool busy)
    {
        UseWaitCursor = busy;
        _btnCancelOp.Enabled = busy;
        foreach (var control in new Control[] { _btnScan, _btnPickSource, _btnPickTarget, _chkAdGroups, _grid, _btnSelectAll, _btnSelectNone })
        {
            control.Enabled = !busy;
        }
        if (busy)
        {
            _btnApply.Enabled = false;
            _btnExport.Enabled = false;
        }
        else
        {
            UpdateButtons();
        }
    }

    private void UpdateButtons()
    {
        _btnScan.Enabled = _cts is null && _source is not null && _target is not null;
        _btnApply.Enabled = _cts is null && _service is not null && CheckedRows().Any();
        _btnExport.Enabled = _cts is null && _grid.Rows.Count > 0;
    }

    private IEnumerable<DataGridViewRow> CheckedRows() =>
        _grid.Rows.Cast<DataGridViewRow>().Where(r => r.Tag is PermissionItem { CanApply: true } && r.Cells[ColApply].Value is true && !r.Cells[ColApply].ReadOnly);

    private void SetAllChecked(bool value)
    {
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (!row.Cells[ColApply].ReadOnly)
            {
                row.Cells[ColApply].Value = value;
            }
        }
        UpdateButtons();
    }

    private void ClearResults()
    {
        _grid.Rows.Clear();
        _service = null;
        _scannedContext = null;
        _lastReport = null;
        _lblStatus.Text = _source is null || _target is null ? "Pick both users, then Scan." : "Ready - press Scan.";
        UpdateButtons();
    }

    private string? SaveReportCsv(PermissionCopyReport report)
    {
        try
        {
            Directory.CreateDirectory(_csvDirectory);
            var path = Path.Combine(_csvDirectory,
                $"{report.StartedLocal:yyyy-MM-dd_HHmmss}_{report.Context.Source.SamAccountName}_to_{report.Context.Target.SamAccountName}.csv");
            File.WriteAllText(path, PermissionCopyService.BuildCsv(report));
            return path;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Exports the last Apply's results if there was one, otherwise the scan as shown.</summary>
    private void ExportCsv()
    {
        var ctx = _lastReport?.Context ?? _scannedContext;
        if (ctx is null)
        {
            return;
        }
        var content = _lastReport is not null
            ? PermissionCopyService.BuildCsv(_lastReport)
            : PermissionCopyService.BuildScanCsv(ctx, _grid.Rows.Cast<DataGridViewRow>().Select(r => r.Tag).OfType<PermissionItem>().ToList());
        using var save = new SaveFileDialog
        {
            Filter = "CSV files (*.csv)|*.csv",
            FileName = $"Permissions_{ctx.Source.SamAccountName}_to_{ctx.Target.SamAccountName}.csv",
        };
        if (save.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }
        try
        {
            File.WriteAllText(save.FileName, content);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not save: {ex.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
