using InventoryManager.Core.Ad;

namespace InventoryManager.App.Dialogs;

/// <summary>
/// Port of the PowerShell build's Show-AdUserSearchDialog: a standalone AD user search, useful on
/// its own (not tied to any inventory row) when you just need to look someone up. Uses the same
/// IAdService.SearchUsersByName every AD-touching dialog in this app already goes through.
/// </summary>
public sealed class AdUserSearchDialog : Form
{
    private readonly IAdService _adService;
    private readonly TextBox _txtSearch = new();
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
    };
    private readonly Label _lblCount = new()
    {
        Text = "Type part of a name (first, last, or both) and press Search or Enter.",
        Location = new Point(10, 12),
        Size = new Size(420, 20),
    };

    public AdUserSearchDialog(IAdService adService)
    {
        _adService = adService;

        Icon = AppIcon.Shared;
        Text = "Search Active Directory";
        Size = new Size(600, 450);
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(500, 300);

        var topPanel = new Panel { Dock = DockStyle.Top, Height = 40 };
        topPanel.Controls.Add(new Label { Text = "Name contains:", Location = new Point(10, 12), Size = new Size(90, 20) });
        _txtSearch.Location = new Point(105, 9);
        _txtSearch.Size = new Size(320, 20);
        topPanel.Controls.Add(_txtSearch);
        var btnSearch = new Button { Text = "Search", Location = new Point(435, 7), Size = new Size(90, 26) };
        btnSearch.Click += (_, _) => DoSearch();
        topPanel.Controls.Add(btnSearch);

        var bottomPanel = new Panel { Dock = DockStyle.Bottom, Height = 40 };
        bottomPanel.Controls.Add(_lblCount);
        var btnClose = new Button { Text = "Close", Location = new Point(490, 6), Size = new Size(80, 28) };
        btnClose.Click += (_, _) => Close();
        bottomPanel.Controls.Add(btnClose);

        Controls.Add(_grid);
        Controls.Add(bottomPanel);
        Controls.Add(topPanel);

        _txtSearch.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                DoSearch();
                e.SuppressKeyPress = true;
            }
        };
        AcceptButton = btnSearch;
    }

    private void DoSearch()
    {
        var term = _txtSearch.Text.Trim();
        if (string.IsNullOrWhiteSpace(term))
        {
            MessageBox.Show(this, "Enter part of a name to search for.", "Search Active Directory", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        Cursor = Cursors.WaitCursor;
        try
        {
            var results = _adService.SearchUsersByName(term)
                .OrderBy(u => u.Surname).ThenBy(u => u.GivenName)
                .Select(u => new { FirstName = u.GivenName, LastName = u.Surname, u.SamAccountName, u.Title })
                .ToList();
            _grid.DataSource = results;
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
}
