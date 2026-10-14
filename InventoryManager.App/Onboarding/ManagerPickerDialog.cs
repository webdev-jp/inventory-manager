using InventoryManager.Core.Ad;

namespace InventoryManager.App.Onboarding;

/// <summary>Port of the PowerShell build's Show-ManagerPickerDialog: live AD name search, single
/// selection. DistinguishedName is fetched but hidden from the grid - only needed as the return
/// value, not something an admin should be picking off of visually.</summary>
public sealed class ManagerPickerDialog : Form
{
    private readonly IAdService _adService;
    private readonly TextBox _txtSearch;
    private readonly DataGridView _grid;
    private readonly Label _lblCount;

    public AdUserSummary? Selected { get; private set; }

    /// <param name="title">Window/message caption - Copy User Permissions reuses this picker to
    /// choose its source and target users.</param>
    public ManagerPickerDialog(IAdService adService, string title = "Select Manager")
    {
        _adService = adService;

        Icon = AppIcon.Shared;
        Text = title;
        // Height kept comfortably under the Onboarding wizard's own 560px height so, when
        // CenterParent'd on it, this dialog's own bottom buttons never land on top of/crowd the
        // wizard's Back/Next/Cancel row underneath - see GroupPickerDialog/OuPickerDialog for the
        // same fix (Francois hit and flagged this on the Security Groups step).
        Size = new Size(600, 380);
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(500, 300);

        var topPanel = new Panel { Dock = DockStyle.Top, Height = 40 };
        var lblSearch = new Label { Text = "Name contains:", Location = new Point(10, 12), Size = new Size(90, 20) };
        _txtSearch = new TextBox { Location = new Point(105, 9), Size = new Size(320, 20) };
        var btnSearch = new Button { Text = "Search", Location = new Point(435, 7), Size = new Size(90, 26) };
        topPanel.Controls.Add(lblSearch);
        topPanel.Controls.Add(_txtSearch);
        topPanel.Controls.Add(btnSearch);

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
        _lblCount = new Label { Text = "Type part of a name (first, last, or both) and press Search or Enter.", Location = new Point(10, 15), Size = new Size(390, 20) };
        var btnSelect = new Button { Text = "Select", Location = new Point(410, 8), Size = new Size(80, 28), DialogResult = DialogResult.OK };
        var btnCancel = new Button { Text = "Cancel", Location = new Point(500, 8), Size = new Size(80, 28), DialogResult = DialogResult.Cancel };
        btnSelect.Click += (_, _) => OnSelect();
        bottomPanel.Controls.Add(_lblCount);
        bottomPanel.Controls.Add(btnSelect);
        bottomPanel.Controls.Add(btnCancel);

        Controls.Add(_grid);
        Controls.Add(bottomPanel);
        Controls.Add(topPanel);
        AcceptButton = btnSelect;
        CancelButton = btnCancel;

        btnSearch.Click += (_, _) => DoSearch();
        _txtSearch.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                DoSearch();
                e.SuppressKeyPress = true;
            }
        };
    }

    private void DoSearch()
    {
        var term = _txtSearch.Text.Trim();
        if (string.IsNullOrWhiteSpace(term))
        {
            MessageBox.Show(this, "Enter part of a name to search for.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        Cursor = Cursors.WaitCursor;
        try
        {
            var results = _adService.SearchUsersByName(term)
                .OrderBy(u => u.Surname).ThenBy(u => u.GivenName)
                .Select(u => new { u.GivenName, u.Surname, u.SamAccountName, u.Title, u.DistinguishedName })
                .ToList();
            _grid.DataSource = results;
            if (_grid.Columns["DistinguishedName"] is { } dnColumn)
            {
                dnColumn.Visible = false;
            }
            _lblCount.Text = $"{results.Count} match(es) found.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Search failed: {ex.Message}", "Active Directory Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            Cursor = Cursors.Default;
        }
    }

    private void OnSelect()
    {
        if (_grid.SelectedRows.Count != 1)
        {
            DialogResult = DialogResult.None;
            return;
        }
        dynamic row = _grid.SelectedRows[0].DataBoundItem!;
        Selected = new AdUserSummary(
            (string)row.SamAccountName,
            (string)row.GivenName,
            (string)row.Surname,
            (string)row.Title,
            (string)row.DistinguishedName);
    }
}
