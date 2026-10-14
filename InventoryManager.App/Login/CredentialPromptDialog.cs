namespace InventoryManager.App.Login;

/// <summary>
/// Port of the PowerShell build's Show-CredentialPromptDialog: a small modal login prompt used for
/// both the gate-account check and the SU login, differing only by title/info text and whether the
/// username field is locked to a fixed value (the gate account, always the same one) or left blank
/// for the operator to type their own (the SU login, a different person each time).
/// </summary>
public sealed class CredentialPromptDialog : Form
{
    private readonly TextBox _txtUsername = new();
    private readonly TextBox _txtPassword = new() { UseSystemPasswordChar = true };

    public sealed record CredentialResult(string Username, string Password);

    public CredentialResult? Result { get; private set; }

    public CredentialPromptDialog(string title, string infoText, string? fixedUsername)
    {
        Icon = AppIcon.Shared;
        Text = title;
        Size = new Size(420, 260);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        TopMost = true;

        Controls.Add(new Label { Text = infoText, Location = new Point(10, 10), Size = new Size(385, 60) });
        Controls.Add(new Label { Text = "Username:", Location = new Point(10, 80), Size = new Size(90, 20) });

        _txtUsername.Location = new Point(105, 77);
        _txtUsername.Size = new Size(285, 20);
        if (fixedUsername is not null)
        {
            _txtUsername.Text = fixedUsername;
            _txtUsername.ReadOnly = true;
            _txtUsername.TabStop = false;
        }
        Controls.Add(_txtUsername);

        Controls.Add(new Label { Text = "Password:", Location = new Point(10, 110), Size = new Size(90, 20) });
        _txtPassword.Location = new Point(105, 107);
        _txtPassword.Size = new Size(285, 20);
        Controls.Add(_txtPassword);

        var btnOk = new Button { Text = "Login", Location = new Point(215, 165), Size = new Size(90, 28), DialogResult = DialogResult.OK };
        var btnCancel = new Button { Text = "Cancel", Location = new Point(310, 165), Size = new Size(90, 28), DialogResult = DialogResult.Cancel };
        Controls.Add(btnOk);
        Controls.Add(btnCancel);
        AcceptButton = btnOk;
        CancelButton = btnCancel;

        FormClosing += (_, _) =>
        {
            if (DialogResult == DialogResult.OK)
            {
                Result = new CredentialResult(_txtUsername.Text.Trim(), _txtPassword.Text);
            }
        };
    }
}
