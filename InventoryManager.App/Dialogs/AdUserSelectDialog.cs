using InventoryManager.Core.Ad;

namespace InventoryManager.App.Dialogs;

/// <summary>
/// "Match to a different user": a small AD search that returns the person you pick. Used by the
/// Add/Edit dialog's Change... button when the automatic match is wrong, ambiguous or missing.
/// Pre-filled with the name already typed and searches immediately, so most of the time the right
/// person is already on screen. The search is by any part of the name (first, last or both), so
/// it also finds someone whose name differs from what was typed.
/// </summary>
public sealed class AdUserSelectDialog : Form
{
    private readonly IAdService _adService;
    private readonly TextBox _txtSearch = new();
    private readonly Label _lblStatus = new() { AutoSize = false };
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        MultiSelect = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        RowHeadersVisible = false,
    };
    private IReadOnlyList<AdUserSummary> _results = [];

    /// <summary>The person the operator picked, or null if they cancelled.</summary>
    public AdUserSummary? Result { get; private set; }

    public AdUserSelectDialog(IAdService adService, string initialTerm, string forWhom)
    {
        _adService = adService;

        Icon = AppIcon.Shared;
        Text = string.IsNullOrWhiteSpace(forWhom) ? "Choose AD User" : $"Choose AD User for {forWhom}";
        Size = new Size(620, 420);
        MinimumSize = new Size(480, 300);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;

        var top = new Panel { Dock = DockStyle.Top, Height = 42 };
        top.Controls.Add(new Label { Text = "Name contains:", Location = new Point(10, 14), Size = new Size(90, 20) });
        _txtSearch.Location = new Point(105, 11);
        _txtSearch.Size = new Size(330, 22);
        _txtSearch.Text = initialTerm;
        top.Controls.Add(_txtSearch);
        var btnSearch = new Button { Text = "Search", Location = new Point(445, 9), Size = new Size(90, 26) };
        btnSearch.Click += (_, _) => DoSearch();
        top.Controls.Add(btnSearch);

        // RightToLeft flow: re-measured every layout pass, so the buttons can't drift off-screen.
        var bottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 46,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(8, 8, 8, 0),
            WrapContents = false,
        };
        var btnCancel = new Button { Text = "Cancel", Size = new Size(90, 30), DialogResult = DialogResult.Cancel };
        var btnSelect = new Button { Text = "Use this user", Size = new Size(120, 30) };
        btnSelect.Click += (_, _) => Accept();
        bottom.Controls.AddRange([btnCancel, btnSelect]);

        _lblStatus.Dock = DockStyle.Bottom;
        _lblStatus.Height = 22;
        _lblStatus.Padding = new Padding(10, 3, 0, 0);

        Controls.Add(_grid);
        Controls.Add(_lblStatus);
        Controls.Add(bottom);
        Controls.Add(top);
        CancelButton = btnCancel;
        AcceptButton = btnSearch;

        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) { Accept(); } };
        _txtSearch.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                DoSearch();
                e.SuppressKeyPress = true;
            }
        };
        Shown += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(_txtSearch.Text))
            {
                DoSearch();
            }
            _txtSearch.Focus();
        };
    }

    private void DoSearch()
    {
        var term = _txtSearch.Text.Trim();
        if (term.Length == 0)
        {
            _lblStatus.Text = "Type part of a name to search.";
            return;
        }
        Cursor = Cursors.WaitCursor;
        try
        {
            _results = _adService.SearchUsersByName(term)
                .OrderBy(u => u.Surname, StringComparer.OrdinalIgnoreCase)
                .ThenBy(u => u.GivenName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            _grid.DataSource = _results
                .Select(u => new { FirstName = u.GivenName, LastName = u.Surname, u.SamAccountName, u.Title })
                .ToList();
            _lblStatus.Text = _results.Count == 0 ? "No matches." : $"{_results.Count} match(es) - select one and click \"Use this user\" (or double-click).";
        }
        catch (Exception ex)
        {
            _lblStatus.Text = $"Search failed: {ex.Message}";
        }
        finally
        {
            Cursor = Cursors.Default;
        }
    }

    private void Accept()
    {
        if (_grid.SelectedRows.Count != 1)
        {
            _lblStatus.Text = "Select a person first.";
            return;
        }
        var index = _grid.SelectedRows[0].Index;
        if (index < 0 || index >= _results.Count)
        {
            return;
        }
        Result = _results[index];
        DialogResult = DialogResult.OK;
        Close();
    }
}
