using InventoryManager.Core.Offboarding;
using InventoryManager.Core.Persistence;

namespace InventoryManager.App.Dialogs;

/// <summary>
/// The pending scheduled terminations, soonest first. Used two ways: opened from File → Scheduled
/// Terminations..., and automatically as the "these are due" prompt (same list, with a headline).
/// "Offboard Now" doesn't run anything itself - it closes the dialog and hands the chosen entry
/// back so MainForm starts the normal supervised Offboard User flow for it.
/// </summary>
public sealed class ScheduledTerminationsDialog : Form
{
    private readonly ScheduledTerminationStore _store;
    private readonly string _me;
    private readonly Func<ScheduledTermination, bool> _edit;
    private readonly Func<ScheduledTermination, bool> _cancel;
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        MultiSelect = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        RowHeadersVisible = false,
        BackgroundColor = SystemColors.Window,
    };
    private readonly Label _headline = new()
    {
        Dock = DockStyle.Top,
        Height = 44,
        Padding = new Padding(12, 10, 12, 0),
        Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold),
    };
    private readonly Button _btnOffboard = new() { Text = "Offboard Now...", Size = new Size(130, 30) };
    private readonly Button _btnEdit = new() { Text = "Edit...", Size = new Size(90, 30) };
    private readonly Button _btnCancelSchedule = new() { Text = "Cancel Schedule...", Size = new Size(130, 30) };
    private readonly Button _btnClose = new() { Text = "Close", Size = new Size(90, 30), DialogResult = DialogResult.Cancel };
    private IReadOnlyList<ScheduledTermination> _rows = [];

    /// <summary>The entry the operator chose to offboard now, or null when the dialog just closed.</summary>
    public ScheduledTermination? OffboardRequested { get; private set; }

    public ScheduledTerminationsDialog(
        ScheduledTerminationStore store,
        string me,
        string? dueHeadline,
        Func<ScheduledTermination, bool> edit,
        Func<ScheduledTermination, bool> cancel)
    {
        _store = store;
        _me = me;
        _edit = edit;
        _cancel = cancel;

        Icon = AppIcon.Shared;
        Text = dueHeadline is null ? "Scheduled Terminations" : "Scheduled Terminations Due";
        Size = new Size(1000, 420);
        MinimumSize = new Size(760, 300);
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = true;
        ShowInTaskbar = dueHeadline is not null;

        _headline.Text = dueHeadline ?? "Terminations scheduled for later. Nothing runs by itself - when one is due, the next admin with the app open is prompted to offboard through the normal tool.";
        _headline.ForeColor = dueHeadline is null ? SystemColors.ControlText : Color.DarkRed;
        if (dueHeadline is null)
        {
            _headline.Font = SystemFonts.DefaultFont;
        }

        _grid.Columns.Add("Due", "Due");
        _grid.Columns.Add("User", "User");
        _grid.Columns.Add("Device", "Device");
        _grid.Columns.Add("Ticket", "Ticket #");
        _grid.Columns.Add("By", "Scheduled by");
        _grid.Columns.Add("Status", "Status");
        _grid.Columns.Add("Notes", "Notes");
        _grid.Columns["Due"]!.FillWeight = 90;
        _grid.Columns["User"]!.FillWeight = 110;
        _grid.Columns["Device"]!.FillWeight = 80;
        _grid.Columns["Ticket"]!.FillWeight = 60;
        _grid.Columns["By"]!.FillWeight = 70;
        _grid.Columns["Status"]!.FillWeight = 100;
        _grid.Columns["Notes"]!.FillWeight = 130;

        // RightToLeft flow: the first control added sits at the far right. Re-measured on every
        // layout pass, so unlike hand-placed anchored buttons it can't drift off-screen on resize.
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 46,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(8, 8, 8, 0),
            WrapContents = false,
        };
        buttons.Controls.AddRange([_btnClose, _btnCancelSchedule, _btnEdit, _btnOffboard]);

        // Docking order: Fill first, then Top/Bottom (later-added claims space first).
        Controls.Add(_grid);
        Controls.Add(buttons);
        Controls.Add(_headline);
        CancelButton = _btnClose;

        _btnOffboard.Click += (_, _) => OnOffboardClicked();
        _btnEdit.Click += (_, _) => RunOnSelected(_edit);
        _btnCancelSchedule.Click += (_, _) => RunOnSelected(_cancel);
        _grid.SelectionChanged += (_, _) => UpdateButtons();
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) { RunOnSelected(_edit); } };

        Reload(selectFirstDue: true);
    }

    private ScheduledTermination? Selected =>
        _grid.SelectedRows.Count == 1 && _grid.SelectedRows[0].Index < _rows.Count ? _rows[_grid.SelectedRows[0].Index] : null;

    private void Reload(bool selectFirstDue = false)
    {
        var keepId = Selected?.Id;
        try
        {
            _rows = _store.LoadPending();
        }
        catch (Exception ex)
        {
            _rows = [];
            MessageBox.Show(this, $"Could not read the scheduled terminations: {ex.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        var now = DateTime.UtcNow;
        _grid.Rows.Clear();
        foreach (var t in _rows)
        {
            var who = string.IsNullOrWhiteSpace(t.DisplayName) ? t.Username : $"{t.DisplayName} ({t.Username})";
            var i = _grid.Rows.Add(
                t.DueAtUtc.ToLocalTime().ToString("ddd M/d/yyyy h:mm tt"),
                who,
                t.DeviceName,
                t.Ticket,
                t.ScheduledBy,
                ScheduledTerminationRules.Describe(t, _me, now),
                t.Notes.Replace("\r\n", " ").Replace('\n', ' '));
            var row = _grid.Rows[i];
            if (ScheduledTerminationRules.IsClaimedByOther(t, _me, now))
            {
                row.DefaultCellStyle.ForeColor = SystemColors.GrayText;
            }
            else if (ScheduledTerminationRules.IsDue(t, now))
            {
                row.DefaultCellStyle.BackColor = Color.FromArgb(255, 224, 178);
                row.DefaultCellStyle.Font = new Font(_grid.Font, FontStyle.Bold);
            }
        }

        _grid.ClearSelection();
        var target = keepId is not null ? _rows.ToList().FindIndex(t => t.Id == keepId) : -1;
        if (target < 0 && selectFirstDue)
        {
            // Preselect something this admin can actually act on - not an entry another admin is
            // already handling.
            target = _rows.ToList().FindIndex(t => ScheduledTerminationRules.IsDue(t, now) && !ScheduledTerminationRules.IsClaimedByOther(t, _me, now));
        }
        if (target < 0 && _rows.Count > 0)
        {
            target = 0;
        }
        if (target >= 0)
        {
            _grid.Rows[target].Selected = true;
            _grid.CurrentCell = _grid.Rows[target].Cells[0];
        }
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var sel = Selected;
        var now = DateTime.UtcNow;
        var handledByOther = sel is not null && ScheduledTerminationRules.IsClaimedByOther(sel, _me, now);
        _btnOffboard.Enabled = sel is not null && !handledByOther;
        _btnEdit.Enabled = sel is not null;
        _btnCancelSchedule.Enabled = sel is not null;
    }

    private void RunOnSelected(Func<ScheduledTermination, bool> action)
    {
        var sel = Selected;
        if (sel is null)
        {
            return;
        }
        if (action(sel))
        {
            Reload();
        }
    }

    private void OnOffboardClicked()
    {
        var sel = Selected;
        if (sel is null)
        {
            return;
        }
        if (!ScheduledTerminationRules.IsDue(sel, DateTime.UtcNow)
            && MessageBox.Show(this,
                $"{sel.Username} isn't scheduled until {sel.DueAtUtc.ToLocalTime():ddd M/d/yyyy h:mm tt}. Offboard now anyway?",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
        {
            return;
        }
        OffboardRequested = sel;
        DialogResult = DialogResult.OK;
        Close();
    }
}
