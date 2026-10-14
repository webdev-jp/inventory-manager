namespace InventoryManager.App.Dialogs;

/// <summary>
/// A single-line text prompt (title + message + one textbox + OK/Cancel) - used where a full
/// custom dialog would be overkill, e.g. asking for a ticket number after an external tool closes.
/// </summary>
public sealed class SimpleTextPromptDialog : Form
{
    private readonly TextBox _txtValue = new();

    public string Value => _txtValue.Text.Trim();

    public SimpleTextPromptDialog(string title, string message)
    {
        Icon = AppIcon.Shared;
        Text = title;
        Size = new Size(420, 180);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        var lblMessage = new Label { Text = message, Location = new Point(15, 15), Size = new Size(390, 40) };
        _txtValue.Location = new Point(15, 60);
        _txtValue.Size = new Size(390, 22);

        var btnOk = new Button { Text = "OK", Location = new Point(225, 100), Size = new Size(85, 28), DialogResult = DialogResult.OK };
        var btnCancel = new Button { Text = "Cancel", Location = new Point(320, 100), Size = new Size(85, 28), DialogResult = DialogResult.Cancel };

        Controls.Add(lblMessage);
        Controls.Add(_txtValue);
        Controls.Add(btnOk);
        Controls.Add(btnCancel);
        AcceptButton = btnOk;
        CancelButton = btnCancel;
    }
}
