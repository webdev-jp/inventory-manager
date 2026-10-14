using InventoryManager.Core.Ad;

namespace InventoryManager.App.Dialogs;

/// <summary>
/// Port of the PowerShell build's Show-AdCredentialDialog: an ad-hoc runtime AD credential
/// override, separate from the login gate - lets the operator run AD writes as a dedicated rights-
/// holding account (e.g. "sujohnsmith") for the rest of the session instead of their own account,
/// without restarting the app. Deliberately does NOT validate the credential against AD (same as
/// the PS source) - it's trusted as entered, same as the PowerShell build's own design.
/// </summary>
public sealed class AdCredentialDialog : Form
{
    private readonly TextBox _txtUsername = new();
    private readonly TextBox _txtPassword = new() { UseSystemPasswordChar = true };
    private readonly string _domain;

    public AdCredentialContext? Result { get; private set; }

    public AdCredentialDialog(string domain, string? currentUsername)
    {
        _domain = domain;

        Icon = AppIcon.Shared;
        Text = "Active Directory Credentials";
        Size = new Size(400, 255);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        Controls.Add(new Label
        {
            Text = "Enter credentials for an account with rights to modify Active Directory (e.g. sujohnsmith), so AD updates don't fail mid-session. Used only in this app, only this session, never saved to disk. Cancel to use your normal Windows session instead.",
            Location = new Point(10, 10),
            Size = new Size(365, 70),
        });

        Controls.Add(new Label { Text = "Username:", Location = new Point(10, 90), Size = new Size(90, 20) });
        _txtUsername.Location = new Point(105, 87);
        _txtUsername.Size = new Size(270, 20);
        if (currentUsername is not null)
        {
            _txtUsername.Text = currentUsername;
        }
        Controls.Add(_txtUsername);

        Controls.Add(new Label { Text = "Password:", Location = new Point(10, 120), Size = new Size(90, 20) });
        _txtPassword.Location = new Point(105, 117);
        _txtPassword.Size = new Size(270, 20);
        Controls.Add(_txtPassword);

        var btnOk = new Button { Text = "OK", Location = new Point(205, 165), Size = new Size(80, 28) };
        var btnCancel = new Button { Text = "Cancel", Location = new Point(290, 165), Size = new Size(80, 28), DialogResult = DialogResult.Cancel };
        btnOk.Click += (_, _) => Accept();
        Controls.Add(btnOk);
        Controls.Add(btnCancel);
        AcceptButton = btnOk;
        CancelButton = btnCancel;
    }

    private void Accept()
    {
        var username = _txtUsername.Text.Trim();
        if (string.IsNullOrWhiteSpace(username))
        {
            MessageBox.Show(this, "Username is required.", "Validation Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var qualified = LoginGateService.QualifyUsername(username, ResolveNetBiosNameBestEffort());
        Result = AdCredentialContext.FromPlainText(qualified, _txtPassword.Text, _domain);
        MessageBox.Show(this, $"Active Directory actions in this app will now run as '{qualified}' until you clear it or close the app.", "AD Credentials Set", MessageBoxButtons.OK, MessageBoxIcon.Information);
        DialogResult = DialogResult.OK;
        Close();
    }

    private string? ResolveNetBiosNameBestEffort()
    {
        try
        {
            return new AdService(_domain).GetDomainNetBiosName();
        }
        catch
        {
            return null;
        }
    }
}
