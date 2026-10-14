using InventoryManager.Core.Ad;

namespace InventoryManager.App.Onboarding;

/// <summary>
/// Port of the PowerShell build's Show-OuPickerDialog: a searchable pick-list, not a navigable
/// tree - simplest thing that works given every remaining OU (after the allow-list filter) is a
/// legitimate option. Restricted to sub-OUs of OuPolicy.AllowedUserOuBaseDn only - onboarding is
/// this dialog's only caller, so scoping it here is safe.
/// </summary>
public sealed class OuPickerDialog : Form
{
    private readonly DataGridView _grid;
    private readonly IReadOnlyList<AdOrganizationalUnitInfo> _allowedOus;

    public string? SelectedDistinguishedName { get; private set; }

    public OuPickerDialog(IAdService adService)
    {
        Icon = AppIcon.Shared;
        Text = "Select Organizational Unit (under Domain Users)";
        // Height kept comfortably under the Onboarding wizard's own 560px height so, when
        // CenterParent'd on it, this dialog's own bottom buttons never land on top of/crowd the
        // wizard's Back/Next/Cancel row underneath (width left as-is - the button Location values
        // just above depend on it).
        Size = new Size(750, 380);
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(500, 300);

        _allowedOus = adService.ListOrganizationalUnits()
            .Where(ou => OuPolicy.IsAllowedForNewUsers(ou.DistinguishedName))
            .OrderBy(ou => ou.DistinguishedName)
            .ToList();

        var topPanel = new Panel { Dock = DockStyle.Top, Height = 36 };
        var lblSearch = new Label { Text = "Search:", Location = new Point(10, 10), Size = new Size(50, 20) };
        var txtSearch = new TextBox { Location = new Point(65, 7), Size = new Size(400, 20) };
        topPanel.Controls.Add(lblSearch);
        topPanel.Controls.Add(txtSearch);

        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            MultiSelect = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        };

        void LoadCandidates()
        {
            var term = txtSearch.Text.Trim();
            var rows = string.IsNullOrWhiteSpace(term)
                ? _allowedOus
                : _allowedOus.Where(ou => ou.Name.Contains(term, StringComparison.OrdinalIgnoreCase) || ou.DistinguishedName.Contains(term, StringComparison.OrdinalIgnoreCase)).ToList();
            _grid.DataSource = rows.Select(ou => new { ou.Name, ou.DistinguishedName }).ToList();
            if (_grid.Rows.Count > 0)
            {
                _grid.Rows[0].Selected = true;
            }
        }

        txtSearch.TextChanged += (_, _) => LoadCandidates();

        // Fixed absolute positions, matching ManagerPickerDialog/GroupPickerDialog's own button
        // layout - NOT computed from this.Width. This dialog previously computed Location from
        // this.Width while adding the buttons to bottomPanel BEFORE bottomPanel itself had been
        // added to the Form and Dock-laid-out (it was still its un-parented default ~200px wide
        // at that moment). Combined with Anchor=Right, WinForms locked in a "distance from the
        // right edge" based on that bogus pre-layout width, so once Dock=Bottom later stretched
        // the panel to the dialog's real ~750px width, the anchor recalculation pushed both
        // buttons far outside the visible window - invisible and unreachable, exactly the "select
        // an OU but can't get past this step" bug Francois hit.
        var bottomPanel = new Panel { Dock = DockStyle.Bottom, Height = 45 };
        var btnSelect = new Button { Text = "Select", Location = new Point(560, 8), Size = new Size(80, 28), DialogResult = DialogResult.OK };
        var btnCancel = new Button { Text = "Cancel", Location = new Point(650, 8), Size = new Size(80, 28), DialogResult = DialogResult.Cancel };
        btnSelect.Click += (_, _) => OnSelect();
        bottomPanel.Controls.Add(btnSelect);
        bottomPanel.Controls.Add(btnCancel);

        Controls.Add(_grid);
        Controls.Add(bottomPanel);
        Controls.Add(topPanel);
        AcceptButton = btnSelect;
        CancelButton = btnCancel;

        LoadCandidates();
    }

    private void OnSelect()
    {
        if (_grid.SelectedRows.Count != 1)
        {
            DialogResult = DialogResult.None;
            return;
        }
        dynamic row = _grid.SelectedRows[0].DataBoundItem!;
        SelectedDistinguishedName = (string)row.DistinguishedName;
    }
}
