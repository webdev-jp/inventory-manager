using InventoryManager.Core;
using InventoryManager.Core.Persistence;

namespace InventoryManager.App.Dialogs;

/// <summary>
/// Port of the PowerShell build's Show-DuplicateDevicesDialog: surfaces every inventory row that
/// shares a Device Name with at least one other row, with Go-to-Selected (jumps to the row in the
/// main grid, on the correct tab) and Delete Selected. Purely local data - no AD involved.
/// </summary>
public sealed class DuplicateDevicesDialog : Form
{
    private readonly InventoryStore _store;
    private readonly Action _saveAndRefresh;
    private readonly Action<int, string> _goToRow;
    private readonly Action<string> _log;
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        MultiSelect = true,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
    };
    private readonly Label _lblInfo = new() { Location = new Point(10, 15), Size = new Size(500, 20) };

    public DuplicateDevicesDialog(InventoryStore store, Action saveAndRefresh, Action<int, string> goToRow, Action<string> log)
    {
        _store = store;
        _saveAndRefresh = saveAndRefresh;
        _goToRow = goToRow;
        _log = log;

        Icon = AppIcon.Shared;
        Text = "Duplicate Devices";
        Size = new Size(900, 550);
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(700, 400);

        var bottomPanel = new Panel { Dock = DockStyle.Bottom, Height = 45 };
        bottomPanel.Controls.Add(_lblInfo);
        var btnGoTo = new Button { Text = "Go to Selected", Location = new Point(520, 8), Size = new Size(110, 28) };
        btnGoTo.Click += (_, _) => GoToSelected();
        bottomPanel.Controls.Add(btnGoTo);
        var btnDelete = new Button { Text = "Delete Selected", Location = new Point(640, 8), Size = new Size(110, 28) };
        btnDelete.Click += (_, _) => DeleteSelected();
        bottomPanel.Controls.Add(btnDelete);
        var btnRefresh = new Button { Text = "Refresh", Location = new Point(760, 8), Size = new Size(80, 28) };
        btnRefresh.Click += (_, _) => RunDuplicateCheck();
        bottomPanel.Controls.Add(btnRefresh);

        Controls.Add(_grid);
        Controls.Add(bottomPanel);

        Shown += (_, _) => RunDuplicateCheck();
    }

    private void RunDuplicateCheck()
    {
        var rows = DuplicateDeviceFinder.FindDuplicates(_store.Items);
        _grid.DataSource = rows
            .Select(r => new { r.Id, r.DeviceName, r.LastName, r.FirstName, Tab = r.StatusLabel, r.StatusKey, r.SerialNumber })
            .ToList();
        if (_grid.Columns["StatusKey"] is { } statusKeyCol) statusKeyCol.Visible = false;
        if (_grid.Columns["Id"] is { } idCol) idCol.Visible = false;
        var groupCount = rows.Select(r => r.DeviceName.Trim().ToLowerInvariant()).Distinct().Count();
        _lblInfo.Text = $"{groupCount} duplicate Device Name(s) across {rows.Count} row(s).";
    }

    private void GoToSelected()
    {
        if (_grid.SelectedRows.Count != 1)
        {
            MessageBox.Show(this, "Select exactly one row to jump to.", "Go to Selected", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var row = (dynamic)_grid.SelectedRows[0].DataBoundItem!;
        _goToRow((int)row.Id, (string)row.StatusKey);
    }

    private void DeleteSelected()
    {
        if (_grid.SelectedRows.Count == 0)
        {
            MessageBox.Show(this, "Select one or more rows to delete.", "No Selection", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var ids = _grid.SelectedRows.Cast<DataGridViewRow>()
            .OrderBy(r => r.Index)
            .Select(r => (int)((dynamic)r.DataBoundItem!).Id)
            .ToList();
        var confirm = MessageBox.Show(this,
            $"Delete {ids.Count} selected duplicate row(s)? This cannot be undone from here (though an automatic backup exists).",
            "Confirm Delete", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes)
        {
            return;
        }
        var removed = _store.Items.RemoveAll(i => ids.Contains(i.Id));
        if (removed > 0)
        {
            _saveAndRefresh();
            _log($"Deleted {removed} duplicate device row(s).");
        }
        RunDuplicateCheck();
    }
}
