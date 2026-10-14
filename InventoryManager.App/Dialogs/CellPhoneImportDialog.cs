using InventoryManager.Core.CellPhones;

namespace InventoryManager.App.Dialogs;

/// <summary>
/// Reviews a carrier device-report CSV matched against Current Inventory by person name (see
/// CellPhoneMatcher) and lets the operator pick exactly which changes to actually apply - nothing
/// is written to inventory just from loading the CSV. Francois, 2026-09-23: "Can I import those and
/// we match what cell phone belongs to what user and I can manually update the inventory?"
/// Deliberately a File-menu dialog rather than a new main-window tab - a tab represents a device
/// Status, and this is a one-off reconciliation pass against an external file, not an ongoing
/// category of device.
/// <para><b>Matched</b> tab: multi-select grid, showing the current vs. incoming Cellphone #/Model
/// side by side - select the rows you want, "Select All" for convenience. <b>Ambiguous</b> tab: a
/// CSV name matched more than one Current Inventory person (e.g. two employees share a name) - pick
/// the right one per row from its own dropdown, or leave "(skip)" to do nothing with that row.
/// <b>Unmatched</b> tab: informational only, no action - typically department/shared lines
/// ("FRONT DESK PHONE") or someone not in Current Inventory at all.</para>
/// </summary>
public sealed class CellPhoneImportDialog : Form
{
    private readonly CellPhoneMatchResult _result;
    private readonly Action _saveAndRefresh;
    private readonly Action<string> _log;

    private readonly DataGridView _gridMatched = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        MultiSelect = true,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
    };
    private readonly DataGridView _gridAmbiguous = new()
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = false,
        MultiSelect = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
    };
    private readonly DataGridView _gridUnmatched = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        MultiSelect = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
    };
    private readonly Label _lblInfo = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(10, 0, 0, 0) };
    private const string SkipOption = "(skip)";

    public CellPhoneImportDialog(CellPhoneMatchResult result, Action saveAndRefresh, Action<string> log)
    {
        _result = result;
        _saveAndRefresh = saveAndRefresh;
        _log = log;

        Icon = AppIcon.Shared;
        Text = "Import Cell Phone Numbers";
        Size = new Size(950, 600);
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(750, 400);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        var tabMatched = new TabPage($"Matched ({result.Matched.Count})");
        tabMatched.Controls.Add(_gridMatched);
        var tabAmbiguous = new TabPage($"Ambiguous ({result.Ambiguous.Count})");
        tabAmbiguous.Controls.Add(_gridAmbiguous);
        var tabUnmatched = new TabPage($"Unmatched ({result.Unmatched.Count})");
        tabUnmatched.Controls.Add(_gridUnmatched);
        tabs.TabPages.Add(tabMatched);
        tabs.TabPages.Add(tabAmbiguous);
        tabs.TabPages.Add(tabUnmatched);

        // 2026-09-23 fix: these 3 buttons used to sit at fixed Location coordinates that needed
        // 910px of width to all be visible, with a MinimumSize floor of only 750 - at or near that
        // minimum, "Apply Selected"/"Close" rendered off the right edge of the dialog and were
        // completely unreachable, even though the Matched/Ambiguous/Unmatched grids still worked
        // fine (Francois: "I have no way to actually import... all I can do is match"). A first fix
        // attempt used Anchor = Top|Right on each button, which is the usual WinForms answer to
        // this - but live-testing that (via a real, briefly-shown dialog, not just a static check)
        // showed the anchored positions coming out badly wrong after a resize, consistently shifted
        // by the same few hundred pixels regardless of which button - some interaction with this
        // dialog's auto-scaling that wasn't worth chasing down further. A FlowLayoutPanel sidesteps
        // the whole class of problem: it recomputes each button's position fresh from the panel's
        // CURRENT size on every layout pass, with nothing "captured" from an earlier, possibly-wrong
        // baseline the way Anchor's distance-from-edge is - so it can't drift the same way.
        var buttonFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Padding = new Padding(0, 8, 8, 0),
        };
        // Added in reverse of the desired left-to-right reading order, since RightToLeft flow
        // places the first-added control at the rightmost slot.
        var btnClose = new Button { Text = "Close", Size = new Size(90, 28), Margin = new Padding(6, 0, 0, 0) };
        btnClose.Click += (_, _) => Close();
        buttonFlow.Controls.Add(btnClose);
        var btnApply = new Button { Text = "Apply Selected", Size = new Size(110, 28), Margin = new Padding(6, 0, 0, 0) };
        btnApply.Click += (_, _) => ApplySelected();
        buttonFlow.Controls.Add(btnApply);
        var btnSelectAll = new Button { Text = "Select All Matched", Size = new Size(130, 28), Margin = new Padding(6, 0, 0, 0) };
        btnSelectAll.Click += (_, _) => _gridMatched.SelectAll();
        buttonFlow.Controls.Add(btnSelectAll);

        var bottomPanel = new Panel { Dock = DockStyle.Bottom, Height = 45 };
        bottomPanel.Controls.Add(_lblInfo);
        bottomPanel.Controls.Add(buttonFlow);

        Controls.Add(tabs);
        Controls.Add(bottomPanel);

        Shown += (_, _) => Populate();
    }

    private void Populate()
    {
        _gridMatched.DataSource = _result.Matched
            .Select(m => new
            {
                m.Item.Id,
                Device = m.Item.DeviceName,
                Person = $"{m.Item.FirstName} {m.Item.LastName}".Trim(),
                CurrentCell = m.Item["CellPhone"],
                NewCell = m.CsvRow.WirelessNumber,
                CurrentModel = m.Item["DeviceModel"],
                NewModel = m.CsvRow.DeviceModel,
            })
            .ToList();
        if (_gridMatched.Columns["Id"] is { } idCol)
        {
            idCol.Visible = false;
        }
        // Pre-selected by default (Francois, 2026-09-23: "I need an option that lets me overwrite
        // the existing numbers and device model properly") - Apply Selected then works as a true
        // one-click "import everything" action out of the box; deselecting a specific row (or
        // clicking in the grid at all) is still how to exclude just that one from the write.
        _gridMatched.SelectAll();

        _gridUnmatched.DataSource = _result.Unmatched
            .Select(r => new { CsvName = r.RawUserName, r.WirelessNumber, r.DeviceModel })
            .ToList();

        _gridAmbiguous.Columns.Clear();
        _gridAmbiguous.Rows.Clear();
        _gridAmbiguous.Columns.Add(new DataGridViewTextBoxColumn { Name = "CsvName", HeaderText = "CSV Name", ReadOnly = true });
        _gridAmbiguous.Columns.Add(new DataGridViewTextBoxColumn { Name = "Wireless", HeaderText = "Wireless #", ReadOnly = true });
        var candidateColumn = new DataGridViewComboBoxColumn { Name = "Candidate", HeaderText = "Which person is this?" };
        _gridAmbiguous.Columns.Add(candidateColumn);
        foreach (var ambiguity in _result.Ambiguous)
        {
            var options = new List<string> { SkipOption };
            options.AddRange(ambiguity.Candidates.Select(c => $"{c.FirstName} {c.LastName} ({c.DeviceName})".Trim()));
            var rowIndex = _gridAmbiguous.Rows.Add(ambiguity.CsvRow.RawUserName, ambiguity.CsvRow.WirelessNumber, SkipOption);
            var row = _gridAmbiguous.Rows[rowIndex];
            row.Tag = ambiguity;
            ((DataGridViewComboBoxCell)row.Cells["Candidate"]).Items.AddRange(options.Cast<object>().ToArray());
            row.Cells["Candidate"].Value = SkipOption;
        }

        UpdateInfoLabel();
    }

    private void UpdateInfoLabel()
    {
        _lblInfo.Text = $"{_result.Matched.Count} matched, {_result.Ambiguous.Count} ambiguous, {_result.Unmatched.Count} unmatched.";
    }

    private void ApplySelected()
    {
        var appliedCount = 0;
        var appliedNames = new List<string>();

        var selectedIds = _gridMatched.SelectedRows.Cast<DataGridViewRow>()
            .Select(r => (int)((dynamic)r.DataBoundItem!).Id)
            .ToHashSet();
        foreach (var match in _result.Matched.Where(m => selectedIds.Contains(m.Item.Id)))
        {
            CellPhoneMatcher.Apply(match.Item, match.CsvRow);
            appliedNames.Add(match.Item.DeviceName);
            appliedCount++;
        }

        foreach (DataGridViewRow row in _gridAmbiguous.Rows)
        {
            if (row.Tag is not CellPhoneAmbiguity ambiguity)
            {
                continue;
            }
            var chosen = row.Cells["Candidate"].Value as string;
            if (string.IsNullOrEmpty(chosen) || chosen == SkipOption)
            {
                continue;
            }
            var candidate = ambiguity.Candidates.FirstOrDefault(c => $"{c.FirstName} {c.LastName} ({c.DeviceName})".Trim() == chosen);
            if (candidate is null)
            {
                continue;
            }
            CellPhoneMatcher.Apply(candidate, ambiguity.CsvRow);
            appliedNames.Add(candidate.DeviceName);
            appliedCount++;
        }

        if (appliedCount == 0)
        {
            MessageBox.Show(this, "No rows selected/resolved - nothing was applied.", "Import Cell Phone Numbers", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _saveAndRefresh();
        _log($"Cell phone import: updated {appliedCount} record(s) from CSV: {string.Join(", ", appliedNames)}.");
        MessageBox.Show(this, $"Updated {appliedCount} record(s).", "Import Cell Phone Numbers", MessageBoxButtons.OK, MessageBoxIcon.Information);
        Close();
    }
}
