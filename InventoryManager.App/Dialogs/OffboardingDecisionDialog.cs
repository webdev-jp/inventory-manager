using InventoryManager.Core.Offboarding;

namespace InventoryManager.App.Dialogs;

public enum OffboardingDecision
{
    DecideLater,
    Finish,
    Undo,
}

/// <summary>
/// Shown once the offboarding tool closes after a REAL run for the device's user: Finish (disable
/// the computer + move the device to Disabled Devices), Undo offboarding (reverse the tool's own
/// changes - the caller asks for a second confirmation), or Decide later (change nothing).
/// Three explicit buttons instead of Yes/No so a single misclick can never undo an offboard.
/// Closing the window or pressing Esc means Decide later.
/// </summary>
public sealed class OffboardingDecisionDialog : Form
{
    public OffboardingDecision Decision { get; private set; } = OffboardingDecision.DecideLater;

    public OffboardingDecisionDialog(string deviceName, string username, OffboardingOutcome outcome)
    {
        Icon = AppIcon.Shared;
        Text = "Offboarding Tool Closed";
        Size = new Size(620, 420);
        MinimumSize = new Size(520, 360);
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;

        var succeeded = outcome.Kind == OffboardingOutcomeKind.Succeeded;
        var headline = succeeded
            ? $"The offboarding tool finished for {username} with no errors."
            : $"The offboarding tool finished for {username}, but {outcome.FailedSteps.Count} step(s) did not complete.";
        var lblHeadline = new Label
        {
            Text = headline,
            Location = new Point(15, 12),
            Size = new Size(575, 22),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            Font = new Font(Font, FontStyle.Bold),
            ForeColor = succeeded ? Color.FromArgb(27, 94, 32) : Color.FromArgb(191, 54, 12),
        };

        var lines = new List<string>();
        if (!succeeded)
        {
            lines.Add("Steps that did not complete:");
            lines.AddRange(outcome.FailedSteps.Select(f => "  - " + f));
            lines.Add("");
        }
        if (outcome.OtherUsers.Count > 0)
        {
            lines.Add($"Note: the tool was ALSO run for {string.Join(", ", outcome.OtherUsers)}. Those users are not affected by anything below.");
            lines.Add("");
        }
        lines.Add($"Finish - disable computer '{deviceName}' in AD, move it to the Disabled Computers OU, move this device to Disabled Devices and add a note to the user's AD Notes" +
                  (string.IsNullOrEmpty(outcome.Ticket) ? "." : $" (ticket #{outcome.Ticket})."));
        lines.Add("");
        lines.Add(outcome.CanUndo
            ? $"Undo offboarding - reverse the {outcome.UndoableChangeCount} change(s) the tool made to {username}. The device and computer are left as they are."
            : "Undo offboarding - not available: the tool made no reversible changes to this user.");
        if (outcome.NotUndoableNotes.Count > 0)
        {
            lines.Add("    Undo can't reverse: " + string.Join(" / ", outcome.NotUndoableNotes.Select(ShortLabel)));
        }
        lines.Add("");
        lines.Add("Decide later - change nothing now.");

        var txtDetails = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Text = string.Join(Environment.NewLine, lines),
            Location = new Point(15, 40),
            Size = new Size(575, 280),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            BackColor = SystemColors.Window,
        };

        var btnFinish = new Button { Text = "Finish offboarding", Size = new Size(150, 30), Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
        var btnUndo = new Button { Text = "Undo offboarding...", Size = new Size(150, 30), Anchor = AnchorStyles.Bottom | AnchorStyles.Right, Enabled = outcome.CanUndo };
        var btnLater = new Button { Text = "Decide later", Size = new Size(120, 30), Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
        btnLater.Location = new Point(470, 332);
        btnUndo.Location = new Point(310, 332);
        btnFinish.Location = new Point(150, 332);

        btnFinish.Click += (_, _) => Choose(OffboardingDecision.Finish);
        btnUndo.Click += (_, _) => Choose(OffboardingDecision.Undo);
        btnLater.Click += (_, _) => Choose(OffboardingDecision.DecideLater);

        Controls.AddRange([lblHeadline, txtDetails, btnFinish, btnUndo, btnLater]);
        CancelButton = btnLater;
        // Errors: Enter must not finish by accident - default to the do-nothing choice.
        AcceptButton = succeeded ? btnFinish : btnLater;
        Shown += (_, _) => (succeeded ? btnFinish : btnLater).Focus();
    }

    private void Choose(OffboardingDecision decision)
    {
        Decision = decision;
        DialogResult = DialogResult.OK;
        Close();
    }

    /// <summary>First sentence of a not-undoable note ("Password was randomized - ..."), so the
    /// summary line stays readable; the full text is shown after the undo runs.</summary>
    private static string ShortLabel(string note)
    {
        var cut = note.IndexOf(" - ", StringComparison.Ordinal);
        var first = cut > 0 ? note[..cut] : note;
        var dot = first.IndexOf(". ", StringComparison.Ordinal);
        return (dot > 0 ? first[..dot] : first).TrimEnd('.');
    }
}
