using InventoryManager.Core.Offboarding;

namespace InventoryManager.App.Dialogs;

/// <summary>
/// Schedule (or edit/remove) a termination for one user. Nothing runs by itself at the scheduled
/// time: the next admin to open Inventory Manager is prompted to offboard through the normal tool,
/// so the dialog says so plainly. A ticket # is required, same as the offboarding tool itself, so
/// it's captured now rather than hunted for on the day. Fixed-size on purpose (no anchoring to get
/// wrong on resize).
/// </summary>
public sealed class ScheduleTerminationDialog : Form
{
    private readonly DateTimePicker _date = new() { Format = DateTimePickerFormat.Short, Width = 120 };
    private readonly DateTimePicker _time = new() { Format = DateTimePickerFormat.Time, ShowUpDown = true, Width = 110 };
    private readonly TextBox _ticket = new() { Width = 260 };
    private readonly TextBox _notes = new() { Multiline = true, Width = 390, Height = 70, ScrollBars = ScrollBars.Vertical };

    public DateTime DueLocal => _date.Value.Date + _time.Value.TimeOfDay;
    public string Ticket => _ticket.Text.Trim();
    public string Notes => _notes.Text.Trim();
    /// <summary>Set when the operator chose "Remove schedule" (edit mode only).</summary>
    public bool RemoveRequested { get; private set; }

    public ScheduleTerminationDialog(string displayName, string username, string deviceName, ScheduledTermination? existing)
    {
        var editing = existing is not null;
        Icon = AppIcon.Shared;
        Text = editing ? "Edit Scheduled Termination" : "Schedule Termination";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(430, editing ? 372 : 352);

        var who = string.IsNullOrWhiteSpace(displayName) ? username : $"{displayName} ({username})";
        Controls.Add(new Label
        {
            Text = who,
            Location = new Point(15, 14),
            Size = new Size(400, 22),
            Font = new Font(Font, FontStyle.Bold),
        });
        Controls.Add(new Label
        {
            Text = $"Device: {deviceName}",
            Location = new Point(15, 38),
            Size = new Size(400, 20),
            ForeColor = SystemColors.GrayText,
        });

        Controls.Add(new Label { Text = "Terminate on:", Location = new Point(15, 74), Size = new Size(85, 20) });
        _date.Location = new Point(105, 70);
        _time.Location = new Point(235, 70);
        Controls.Add(_date);
        Controls.Add(_time);

        Controls.Add(new Label { Text = "Ticket #:", Location = new Point(15, 108), Size = new Size(85, 20) });
        _ticket.Location = new Point(105, 105);
        Controls.Add(_ticket);

        Controls.Add(new Label { Text = "Notes (optional):", Location = new Point(15, 140), Size = new Size(200, 20) });
        _notes.Location = new Point(15, 162);
        Controls.Add(_notes);

        Controls.Add(new Label
        {
            Text = "Nothing happens automatically. At this time, the next admin who has Inventory Manager open (or opens it) is prompted to offboard this user with the normal Offboarding tool.",
            Location = new Point(15, 242),
            Size = new Size(400, 52),
            ForeColor = Color.FromArgb(120, 60, 0),
        });

        if (existing is not null)
        {
            var due = existing.DueAtUtc.ToLocalTime();
            _date.Value = due.Date;
            _time.Value = due;
            _ticket.Text = existing.Ticket;
            _notes.Text = existing.Notes;
            Controls.Add(new Label
            {
                Text = $"Scheduled by {existing.ScheduledBy} on {existing.ScheduledAtUtc.ToLocalTime():M/d/yyyy h:mm tt}.",
                Location = new Point(15, 298),
                Size = new Size(400, 20),
                ForeColor = SystemColors.GrayText,
            });
        }
        else
        {
            var tomorrow = DateTime.Now.Date.AddDays(1);
            _date.Value = tomorrow;
            _time.Value = tomorrow.AddHours(17);
        }

        var btnSave = new Button { Text = editing ? "Save changes" : "Schedule", Size = new Size(110, 30), Location = new Point(190, ClientSize.Height - 44) };
        var btnCancel = new Button { Text = "Cancel", Size = new Size(100, 30), Location = new Point(310, ClientSize.Height - 44), DialogResult = DialogResult.Cancel };
        btnSave.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(Ticket))
            {
                MessageBox.Show(this, "Enter the ticket number for this termination.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                _ticket.Focus();
                return;
            }
            if (DueLocal <= DateTime.Now.AddMinutes(1))
            {
                MessageBox.Show(this, "That time has already passed. Pick a future date and time - to offboard right now, use Offboard User instead.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            DialogResult = DialogResult.OK;
            Close();
        };
        Controls.Add(btnSave);
        Controls.Add(btnCancel);
        AcceptButton = btnSave;
        CancelButton = btnCancel;

        if (editing)
        {
            var btnRemove = new Button { Text = "Remove schedule", Size = new Size(120, 30), Location = new Point(15, ClientSize.Height - 44), ForeColor = Color.DarkRed };
            btnRemove.Click += (_, _) =>
            {
                if (MessageBox.Show(this, $"Remove the scheduled termination for {username}? Nothing is changed on the account.", Text,
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                {
                    return;
                }
                RemoveRequested = true;
                DialogResult = DialogResult.OK;
                Close();
            };
            Controls.Add(btnRemove);
        }
    }
}
