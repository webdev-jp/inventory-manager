using InventoryManager.App.Onboarding;
using InventoryManager.Core.Ad;
using InventoryManager.Core.Models;

namespace InventoryManager.App.Transfer;

public sealed record TransferDialogResult(
    string TicketNumber,
    bool ManagerChanged, string? NewManagerDn, string? NewManagerDisplay,
    bool OuChanged, string? NewOuDn,
    bool ComputerChanged, InventoryItem? NewComputerItem,
    bool DescriptionChanged, string NewDescription,
    bool OfficeChanged, string NewOffice);

/// <summary>
/// Port of the PowerShell build's Show-TransferUserDialog: Manager/OU/Computer are each
/// independently optional (a "No change" checkbox per field, starting UNCHECKED so the admin must
/// make an explicit choice for each one), while Description/Office are plain editable textboxes
/// pre-filled with the current value - no picker, no checkbox, a "change" is just detected by
/// comparing the final text back to what was there when the dialog opened.
///
/// Unlike the PowerShell version, this needs no mutable-hashtable workaround for its Browse/
/// checkbox handlers - the PowerShell build's own closure-scoping bug (documented in
/// OnboardingWizardForm's own remarks) doesn't exist in C#, so ordinary private fields work.
///
/// When <paramref name="hasAdUser"/> is false (the device has no linked AD username), Manager/OU/
/// Description/Office are all locked to "no change" - there's no AD account to write those
/// attributes to - and only Computer stays editable, mirroring the old computer-only Transfer's
/// capability so it isn't lost now that there's one consolidated action.
/// </summary>
public sealed class TransferUserDialog : Form
{
    private readonly IAdService _adService;
    private readonly List<InventoryItem> _allItems;
    private readonly InventoryItem _currentDeviceItem;
    private readonly string _fullName;

    private readonly TextBox _txtTicket = new();
    private readonly TextBox _txtManagerDisplay = new() { ReadOnly = true };
    private readonly Button _btnBrowseManager = new() { Text = "Browse..." };
    private readonly CheckBox _chkManagerNoChange = new() { Text = "No change to Manager" };
    private readonly TextBox _txtOuDisplay = new() { ReadOnly = true };
    private readonly Button _btnBrowseOu = new() { Text = "Browse..." };
    private readonly CheckBox _chkOuNoChange = new() { Text = "No change to OU" };
    private readonly TextBox _txtComputerDisplay = new() { ReadOnly = true };
    private readonly Button _btnBrowseComputer = new() { Text = "Browse..." };
    private readonly CheckBox _chkComputerNoChange = new() { Text = "No change to Computer" };
    private readonly TextBox _txtDescription = new();
    private readonly TextBox _txtOffice = new();
    private readonly Label _lblHint = new() { ForeColor = Color.DimGray };

    private readonly string _currentManagerDisplay;
    private readonly string _currentOuDn;
    private readonly string _currentDescription;
    private readonly string _currentOffice;

    private string? _newManagerDn;
    private string? _newManagerDisplay;
    private string? _newOuDn;
    private InventoryItem? _newComputerItem;

    public TransferDialogResult? Result { get; private set; }

    public TransferUserDialog(
        IAdService adService, List<InventoryItem> allItems, InventoryItem currentDeviceItem, string fullName,
        string currentManagerDisplay, string currentOuDn, string currentDescription, string currentOffice, bool hasAdUser)
    {
        _adService = adService;
        _allItems = allItems;
        _currentDeviceItem = currentDeviceItem;
        _fullName = fullName;
        _currentManagerDisplay = currentManagerDisplay;
        _currentOuDn = currentOuDn;
        _currentDescription = currentDescription;
        _currentOffice = currentOffice;

        Icon = AppIcon.Shared;
        Text = $"Transfer User: {fullName}";
        Size = new Size(640, 500);
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(560, 460);

        AddLabeled("Ticket #:", _txtTicket, 20, 200);

        AddLabeled("Manager:", _txtManagerDisplay, 70, 340);
        _btnBrowseManager.Location = new Point(500, 65);
        _btnBrowseManager.Size = new Size(100, 26);
        _chkManagerNoChange.Location = new Point(150, 95);
        _chkManagerNoChange.Size = new Size(250, 20);
        Controls.Add(_btnBrowseManager);
        Controls.Add(_chkManagerNoChange);
        _txtManagerDisplay.Text = $"(not yet selected - current: {currentManagerDisplay})";
        _btnBrowseManager.Click += (_, _) =>
        {
            using var dialog = new ManagerPickerDialog(_adService);
            if (dialog.ShowDialog(this) == DialogResult.OK && dialog.Selected is { } picked)
            {
                _newManagerDn = picked.DistinguishedName;
                _newManagerDisplay = $"{picked.GivenName} {picked.Surname} ({picked.SamAccountName})";
                _txtManagerDisplay.Text = $"New: {_newManagerDisplay}";
            }
        };
        _chkManagerNoChange.CheckedChanged += (_, _) =>
        {
            if (_chkManagerNoChange.Checked)
            {
                _btnBrowseManager.Enabled = false;
                _newManagerDn = null;
                _newManagerDisplay = null;
                _txtManagerDisplay.Text = $"(no change - current: {currentManagerDisplay})";
            }
            else
            {
                _btnBrowseManager.Enabled = true;
                _txtManagerDisplay.Text = $"(not yet selected - current: {currentManagerDisplay})";
            }
        };

        AddLabeled("Organizational Unit:", _txtOuDisplay, 135, 340);
        _btnBrowseOu.Location = new Point(500, 130);
        _btnBrowseOu.Size = new Size(100, 26);
        _chkOuNoChange.Location = new Point(150, 160);
        _chkOuNoChange.Size = new Size(250, 20);
        Controls.Add(_btnBrowseOu);
        Controls.Add(_chkOuNoChange);
        _txtOuDisplay.Text = $"(not yet selected - current: {currentOuDn})";
        _btnBrowseOu.Click += (_, _) =>
        {
            using var dialog = new OuPickerDialog(_adService);
            if (dialog.ShowDialog(this) == DialogResult.OK && dialog.SelectedDistinguishedName is { } dn)
            {
                _newOuDn = dn;
                _txtOuDisplay.Text = $"New: {dn}";
            }
        };
        _chkOuNoChange.CheckedChanged += (_, _) =>
        {
            if (_chkOuNoChange.Checked)
            {
                _btnBrowseOu.Enabled = false;
                _newOuDn = null;
                _txtOuDisplay.Text = $"(no change - current: {currentOuDn})";
            }
            else
            {
                _btnBrowseOu.Enabled = true;
                _txtOuDisplay.Text = $"(not yet selected - current: {currentOuDn})";
            }
        };

        AddLabeled("Computer:", _txtComputerDisplay, 200, 340);
        _btnBrowseComputer.Location = new Point(500, 195);
        _btnBrowseComputer.Size = new Size(100, 26);
        _chkComputerNoChange.Location = new Point(150, 225);
        _chkComputerNoChange.Size = new Size(250, 20);
        Controls.Add(_btnBrowseComputer);
        Controls.Add(_chkComputerNoChange);
        _txtComputerDisplay.Text = $"(not yet selected - current: {currentDeviceItem.DeviceName})";
        _btnBrowseComputer.Click += (_, _) =>
        {
            using var dialog = new TransferTargetDialog(_allItems, _currentDeviceItem.Id, $"Select New Computer for '{fullName}'");
            if (dialog.ShowDialog(this) == DialogResult.OK && dialog.Selected is { } picked)
            {
                _newComputerItem = picked;
                _txtComputerDisplay.Text = $"New: {picked.DeviceName}";
            }
        };
        _chkComputerNoChange.CheckedChanged += (_, _) =>
        {
            if (_chkComputerNoChange.Checked)
            {
                _btnBrowseComputer.Enabled = false;
                _newComputerItem = null;
                _txtComputerDisplay.Text = $"(no change - current: {currentDeviceItem.DeviceName})";
            }
            else
            {
                _btnBrowseComputer.Enabled = true;
                _txtComputerDisplay.Text = $"(not yet selected - current: {currentDeviceItem.DeviceName})";
            }
        };

        AddLabeled("Description:", _txtDescription, 265, 450);
        _txtDescription.Text = currentDescription;
        AddLabeled("Office:", _txtOffice, 300, 450);
        _txtOffice.Text = currentOffice;

        if (!hasAdUser)
        {
            _chkManagerNoChange.Checked = true;
            _chkManagerNoChange.Enabled = false;
            _chkOuNoChange.Checked = true;
            _chkOuNoChange.Enabled = false;
            _txtDescription.Enabled = false;
            _txtOffice.Enabled = false;
        }

        _lblHint.Text = hasAdUser
            ? "For Manager/OU/Computer: either select a new value via Browse, or check 'No change'."
            : "No linked AD username on this device - only Computer can be transferred. Use Match User first to also edit Manager/OU/Description/Office.";
        _lblHint.Location = new Point(20, 340);
        _lblHint.Size = new Size(580, 20);
        Controls.Add(_lblHint);

        var bottomPanel = new Panel { Dock = DockStyle.Bottom, Height = 50 };
        var btnOk = new Button { Text = "Transfer", Location = new Point(440, 10), Size = new Size(90, 30) };
        var btnCancel = new Button { Text = "Cancel", Location = new Point(535, 10), Size = new Size(90, 30), DialogResult = DialogResult.Cancel };
        btnOk.Click += (_, _) => OnTransfer();
        bottomPanel.Controls.Add(btnOk);
        bottomPanel.Controls.Add(btnCancel);
        Controls.Add(bottomPanel);
        AcceptButton = btnOk;
        CancelButton = btnCancel;
    }

    private void AddLabeled(string label, TextBox control, int y, int width)
    {
        Controls.Add(new Label { Text = label, Location = new Point(20, y), Size = new Size(120, 20) });
        control.Location = new Point(150, y - 3);
        control.Size = new Size(width, 20);
        Controls.Add(control);
    }

    private void OnTransfer()
    {
        if (string.IsNullOrWhiteSpace(_txtTicket.Text))
        {
            MessageBox.Show(this, "Enter a ticket number.", "Transfer User", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (!_chkManagerNoChange.Checked && _newManagerDn is null)
        {
            MessageBox.Show(this, "Select a new Manager, or check 'No change to Manager'.", "Transfer User", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (!_chkOuNoChange.Checked && _newOuDn is null)
        {
            MessageBox.Show(this, "Select a new Organizational Unit, or check 'No change to OU'.", "Transfer User", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (!_chkComputerNoChange.Checked && _newComputerItem is null)
        {
            MessageBox.Show(this, "Select a new Computer, or check 'No change to Computer'.", "Transfer User", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var descriptionChanged = _txtDescription.Text.Trim() != _currentDescription.Trim();
        var officeChanged = _txtOffice.Text.Trim() != _currentOffice.Trim();
        if (_chkManagerNoChange.Checked && _chkOuNoChange.Checked && _chkComputerNoChange.Checked && !descriptionChanged && !officeChanged)
        {
            MessageBox.Show(this, "Nothing was selected to change - select at least one new value, edit Description/Office, or Cancel.", "Transfer User", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Result = new TransferDialogResult(
            TicketNumber: _txtTicket.Text.Trim(),
            ManagerChanged: !_chkManagerNoChange.Checked,
            NewManagerDn: _newManagerDn,
            NewManagerDisplay: _newManagerDisplay,
            OuChanged: !_chkOuNoChange.Checked,
            NewOuDn: _newOuDn,
            ComputerChanged: !_chkComputerNoChange.Checked,
            NewComputerItem: _newComputerItem,
            DescriptionChanged: descriptionChanged,
            NewDescription: _txtDescription.Text.Trim(),
            OfficeChanged: officeChanged,
            NewOffice: _txtOffice.Text.Trim());
        DialogResult = DialogResult.OK;
        Close();
    }
}
