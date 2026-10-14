using InventoryManager.Core.Models;

namespace InventoryManager.App.Transfer;

/// <summary>
/// Port of the PowerShell build's Show-TransferTargetDialog: candidate replacements are ANY
/// inventory device with a blank Description, regardless of which tab it's in - a blank
/// Description is this app's existing signal that nobody's currently assigned to a device, so it
/// doubles as "available" here too.
/// <para><b>Disabled Devices is always included regardless of Description</b> (Francois,
/// 2026-09-22 - "no matter who it's assigned to or its status"): a device the generic Move-to
/// dropdown sent to Disabled Devices keeps whatever Description/assignee it had at the time (only
/// Transfer's own computer handoff and Offboard User actually clear it), so the blank-Description
/// "available" signal alone was silently hiding real, genuinely-disabled devices from this picker
/// - even though picking one here re-enables it as part of the same transfer (see
/// ComputerIdentityHandoff.Apply's conditional enable/disable). A device sitting in Disabled
/// Devices is always a valid transfer/onboarding target, whatever it still shows as assigned.</para>
/// </summary>
public sealed class TransferTargetDialog : Form
{
    private readonly List<InventoryItem> _allItems;
    private readonly int _excludeId;
    private readonly IReadOnlyCollection<string>? _allowedStatuses;
    private readonly TextBox _txtSearch;
    private readonly DataGridView _grid;
    private readonly Label _lblCount;

    public InventoryItem? Selected { get; private set; }

    /// <summary>allowedStatuses restricts the candidate list to specific tabs (e.g. the Onboarding
    /// wizard's Computer Assignment step only wants devices coming from IT Stock/Disabled Devices,
    /// never Current - a device already assigned to someone shouldn't be handed to a new hire from
    /// this picker) - Francois, 2026-09-16. Null (Transfer User's own usage) keeps the original
    /// behavior of any status with a blank Description.</summary>
    public TransferTargetDialog(List<InventoryItem> allItems, int excludeId, string title, IReadOnlyCollection<string>? allowedStatuses = null)
    {
        _allItems = allItems;
        _excludeId = excludeId;
        _allowedStatuses = allowedStatuses;

        Icon = AppIcon.Shared;
        Text = title;
        // Height kept comfortably under the Onboarding wizard's own 560px height so, when
        // CenterParent'd on it (this dialog is now also opened from the wizard's Computer
        // Assignment step, not just Transfer User's much larger MainForm), its own Select/Cancel
        // buttons never land on top of the wizard's Back/Next/Cancel row underneath - same overlap
        // fix already applied to OuPickerDialog/ManagerPickerDialog/GroupPickerDialog. Harmless
        // when opened from the (maximized) MainForm, which has ample vertical room regardless.
        Size = new Size(700, 380);
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(500, 300);

        var topPanel = new Panel { Dock = DockStyle.Top, Height = 36 };
        var lblSearch = new Label { Text = "Search:", Location = new Point(10, 10), Size = new Size(50, 20) };
        _txtSearch = new TextBox { Location = new Point(65, 7), Size = new Size(300, 20) };
        _lblCount = new Label { Location = new Point(380, 10), Size = new Size(290, 20), ForeColor = Color.Gray };
        topPanel.Controls.Add(lblSearch);
        topPanel.Controls.Add(_txtSearch);
        topPanel.Controls.Add(_lblCount);

        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            MultiSelect = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        };

        var bottomPanel = new Panel { Dock = DockStyle.Bottom, Height = 45 };
        var btnSelect = new Button { Text = "Select", Location = new Point(510, 8), Size = new Size(80, 28), DialogResult = DialogResult.OK };
        var btnCancel = new Button { Text = "Cancel", Location = new Point(600, 8), Size = new Size(80, 28), DialogResult = DialogResult.Cancel };
        btnSelect.Click += (_, _) => OnSelect();
        bottomPanel.Controls.Add(btnSelect);
        bottomPanel.Controls.Add(btnCancel);

        Controls.Add(_grid);
        Controls.Add(bottomPanel);
        Controls.Add(topPanel);
        AcceptButton = btnSelect;
        CancelButton = btnCancel;

        _txtSearch.TextChanged += (_, _) => LoadCandidates();
        LoadCandidates();
    }

    private void LoadCandidates()
    {
        var term = _txtSearch.Text.Trim();
        var rows = _allItems.Where(i => i.Id != _excludeId && (string.IsNullOrWhiteSpace(i.Description) || i.Status == "DisabledDevices"));
        if (_allowedStatuses is not null)
        {
            rows = rows.Where(i => _allowedStatuses.Contains(i.Status));
        }
        if (!string.IsNullOrWhiteSpace(term))
        {
            rows = rows.Where(i =>
                i.DeviceName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                i["Model"].Contains(term, StringComparison.OrdinalIgnoreCase) ||
                i["SerialNumber"].Contains(term, StringComparison.OrdinalIgnoreCase));
        }
        // Description is shown so a Disabled Devices row that still carries a stale assignee (see
        // the class doc comment above) is transparent about it, rather than silently offering up a
        // device that LOOKS unassigned in this list but actually isn't yet.
        var display = rows
            .OrderBy(i => i.DeviceName)
            .Select(i => new { i.Id, i.DeviceName, i.Status, Model = i["Model"], SerialNumber = i["SerialNumber"], i.Description })
            .ToList();
        _grid.DataSource = display;
        if (_grid.Columns["Id"] is { } idColumn)
        {
            idColumn.Visible = false;
        }
        _lblCount.Text = $"{display.Count} candidate device(s)";
    }

    private void OnSelect()
    {
        if (_grid.SelectedRows.Count != 1)
        {
            DialogResult = DialogResult.None;
            return;
        }
        dynamic row = _grid.SelectedRows[0].DataBoundItem!;
        int id = row.Id;
        Selected = _allItems.FirstOrDefault(i => i.Id == id);
    }
}
