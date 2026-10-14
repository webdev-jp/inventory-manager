using InventoryManager.Core.Ad;

namespace InventoryManager.App.Onboarding;

/// <summary>
/// Group picker for Onboarding's Security Groups step: loads every group in the domain once when
/// opened (no search required first - Francois wanted the full scrollable, multi-select list back
/// after finding the earlier search-first design too restrictive), with the search box acting as
/// a live, in-memory filter over that already-fetched list rather than a fresh AD query per
/// keystroke. Privileged groups (PrivilegedGroupPolicy) only appear here when this session's SU
/// account is on PrivilegedGroupGrantPolicy's allow-list - this is a convenience filter only, and
/// is completely unchanged by this redesign; the real enforcement is OnboardingService re-checking
/// every group again (against the same policy) right before Add-ADGroupMember, independent of
/// whatever this picker showed.
/// </summary>
public sealed class GroupPickerDialog : Form
{
    private readonly IAdService _adService;
    private readonly bool _canPickPrivilegedGroups;
    private readonly TextBox _txtFilter;
    private readonly DataGridView _grid;
    private readonly Label _lblCount;
    private readonly string? _domainSid;
    private List<AdGroupSummary> _allGroups = [];

    public IReadOnlyList<string> SelectedGroupNames { get; private set; } = [];

    public GroupPickerDialog(IAdService adService, bool canPickPrivilegedGroups)
    {
        _adService = adService;
        _canPickPrivilegedGroups = canPickPrivilegedGroups;
        _domainSid = adService.GetDomainSid();

        Icon = AppIcon.Shared;
        Text = "Add Security Groups";
        // Height kept comfortably under the Onboarding wizard's own 560px height so, when
        // CenterParent'd on it, this dialog's own bottom buttons never land on top of/crowd the
        // wizard's Back/Next/Cancel row underneath - the "overfill" Francois flagged on this exact
        // step.
        Size = new Size(600, 380);
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(500, 300);

        var topPanel = new Panel { Dock = DockStyle.Top, Height = 40 };
        var lblFilter = new Label { Text = "Filter (optional):", Location = new Point(10, 12), Size = new Size(110, 20) };
        _txtFilter = new TextBox { Location = new Point(125, 9), Size = new Size(340, 20) };
        var btnClearFilter = new Button { Text = "✕", Location = new Point(473, 8), Size = new Size(24, 22) };
        btnClearFilter.Click += (_, _) => _txtFilter.Clear();
        topPanel.Controls.Add(lblFilter);
        topPanel.Controls.Add(_txtFilter);
        topPanel.Controls.Add(btnClearFilter);

        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            MultiSelect = true,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        };

        var bottomPanel = new Panel { Dock = DockStyle.Bottom, Height = 45 };
        _lblCount = new Label { Text = "Loading groups...", Location = new Point(10, 15), Size = new Size(370, 20) };
        var btnAdd = new Button { Text = "Add Selected", Location = new Point(390, 8), Size = new Size(100, 28), DialogResult = DialogResult.OK };
        var btnCancel = new Button { Text = "Cancel", Location = new Point(500, 8), Size = new Size(80, 28), DialogResult = DialogResult.Cancel };
        btnAdd.Click += (_, _) => OnAdd();
        bottomPanel.Controls.Add(_lblCount);
        bottomPanel.Controls.Add(btnAdd);
        bottomPanel.Controls.Add(btnCancel);

        Controls.Add(_grid);
        Controls.Add(bottomPanel);
        Controls.Add(topPanel);
        AcceptButton = btnAdd;
        CancelButton = btnCancel;

        _txtFilter.TextChanged += (_, _) => ApplyFilter();

        LoadAllGroups();
    }

    private void LoadAllGroups()
    {
        Cursor = Cursors.WaitCursor;
        try
        {
            // Blank term = every group in the domain, fetched once - see SearchGroupsByName's own
            // doc comment. Sid now comes back in the same query (AdGroupSummary.Sid), so filtering
            // hundreds of groups by privilege below never needs a per-group AD round trip.
            _allGroups = _adService.SearchGroupsByName("").ToList();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not load groups: {ex.Message}", "Active Directory Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _allGroups = [];
        }
        finally
        {
            Cursor = Cursors.Default;
        }
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var term = _txtFilter.Text.Trim();
        var matching = string.IsNullOrWhiteSpace(term)
            ? _allGroups
            : _allGroups.Where(g => g.Name.Contains(term, StringComparison.OrdinalIgnoreCase)).ToList();
        var allowed = matching
            .Where(g => _canPickPrivilegedGroups || !PrivilegedGroupPolicy.IsPrivileged(g.Name, g.Sid, _domainSid))
            .OrderBy(g => g.Name)
            .Select(g => new { g.Name, g.DistinguishedName })
            .ToList();
        _grid.DataSource = allowed;
        if (_grid.Columns["DistinguishedName"] is { } dnColumn)
        {
            dnColumn.Visible = false;
        }
        var hiddenCount = matching.Count - allowed.Count;
        _lblCount.Text = hiddenCount > 0
            ? $"{allowed.Count} group(s) shown ({hiddenCount} privileged group(s) hidden)."
            : $"{allowed.Count} group(s) shown.";
    }

    private void OnAdd()
    {
        if (_grid.SelectedRows.Count == 0)
        {
            DialogResult = DialogResult.None;
            return;
        }
        SelectedGroupNames = _grid.SelectedRows.Cast<DataGridViewRow>()
            .Select(r => (string)((dynamic)r.DataBoundItem!).Name)
            .ToList();
    }
}
