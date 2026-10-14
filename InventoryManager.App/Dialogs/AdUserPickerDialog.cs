using InventoryManager.Core.Ad;

namespace InventoryManager.App.Dialogs;

/// <summary>Port of Show-AdUserPickerDialog: shown when Check AD / Match User's exact-surname
/// search can't narrow to one candidate by First Name alone - a human picks.</summary>
public sealed class AdUserPickerDialog : Form
{
    private readonly IReadOnlyList<AdUserSummary> _candidates;
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        MultiSelect = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
    };

    public AdUserSummary? Result { get; private set; }

    public AdUserPickerDialog(IReadOnlyList<AdUserSummary> candidates, string deviceName)
    {
        _candidates = candidates;

        Icon = AppIcon.Shared;
        Text = $"Select AD User for '{deviceName}'";
        Size = new Size(500, 350);
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(400, 250);

        _grid.DataSource = candidates
            .Select(c => new { FirstName = c.GivenName, LastName = c.Surname, c.SamAccountName })
            .ToList();

        var bottomPanel = new Panel { Dock = DockStyle.Bottom, Height = 45 };
        var btnSelect = new Button { Text = "Select", Location = new Point(310, 8), Size = new Size(80, 28) };
        btnSelect.Click += (_, _) => Accept();
        bottomPanel.Controls.Add(btnSelect);
        var btnCancel = new Button { Text = "Cancel", Location = new Point(400, 8), Size = new Size(80, 28), DialogResult = DialogResult.Cancel };
        bottomPanel.Controls.Add(btnCancel);

        Controls.Add(_grid);
        Controls.Add(bottomPanel);
        AcceptButton = btnSelect;
        CancelButton = btnCancel;
    }

    private void Accept()
    {
        if (_grid.SelectedRows.Count != 1)
        {
            return;
        }
        var selectedSam = (string)_grid.SelectedRows[0].Cells["SamAccountName"].Value!;
        Result = _candidates.FirstOrDefault(c => c.SamAccountName == selectedSam);
        DialogResult = DialogResult.OK;
        Close();
    }
}
