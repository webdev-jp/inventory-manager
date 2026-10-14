using InventoryManager.Core;
using InventoryManager.Core.Security;
using InventoryManager.Core.Ad;
using InventoryManager.Core.Logging;
using InventoryManager.Core.Models;
using InventoryManager.Core.Onboarding;
using InventoryManager.Core.Persistence;
using InventoryManager.App.Transfer;

namespace InventoryManager.App.Onboarding;

/// <summary>
/// Port of the PowerShell build's Show-OnboardingWizard: a true 7-step Next/Back wizard (this
/// app's only multi-step wizard, everywhere else is a single dialog - kept that way here too).
/// Unlike the PowerShell version, plain C# closures over this form's fields work correctly with
/// no special handling - the PowerShell build needed $script: scoping or GetNewClosure() to work
/// around a real closure-scoping bug in bare scriptblock event handlers; that bug doesn't exist
/// here, so every step's state is just a private field read/written directly from event handlers.
/// </summary>
public sealed class OnboardingWizardForm : Form
{
    private static readonly string[] StepTitles =
    [
        "Basic Info", "Account Details", "Organizational Unit", "Security Groups",
        "Computer Assignment", "Inventory Details", "Review & Create",
    ];

    private static readonly string[] ExcludedFromInventoryStep =
        ["DeviceName", "LastName", "FirstName", "Description", "Username"];

    /// <summary>The Onboarding wizard's Computer Assignment step should only ever offer devices
    /// that aren't already in active service - the same source tabs
    /// MainForm.DeviceReturnToServiceSources treats as "returning to service", just from the
    /// opposite direction (a device becoming newly assigned rather than one being re-enabled) -
    /// Francois, 2026-09-16. "Current" is deliberately excluded: a device already assigned to
    /// someone shouldn't be handed to a new hire from this picker. (Unknown/Terminated removed
    /// 2026-09-23 along with their tabs.)</summary>
    private static readonly string[] OnboardingComputerSourceStatuses =
        ["ITStock", "DisabledDevices"];

    private const string DefaultCompany = "Contoso";

    private readonly IAdService _adService;
    private readonly InventoryStore _store;
    private readonly string _upnSuffix;
    private readonly string _suUsername;
    private readonly ISecurityAudit _audit;
    private readonly Action<string, AppLogLevel> _log;

    private readonly Panel[] _steps;
    private readonly Label _lblStepTitle;
    private readonly Button _btnBack;
    private readonly Button _btnNext;
    private int _currentStep;

    // ---- Step 1: Basic Info ----
    private readonly TextBox _txtTicket = new();
    private readonly TextBox _txtFirstName = new();
    private readonly TextBox _txtLastName = new();
    private readonly TextBox _txtDescription = new();
    private readonly TextBox _txtOffice = new();
    private readonly TextBox _txtJobTitle = new();
    private readonly TextBox _txtDepartment = new();
    private readonly TextBox _txtCompany = new() { Text = DefaultCompany, ReadOnly = true };
    private readonly CheckBox _chkChangeOrganization = new() { Text = "Change Organization" };

    // ---- Step 2: Account Details ----
    private readonly TextBox _txtSam = new();
    private readonly Label _lblUpnValue = new();
    private readonly TextBox _txtPassword = new() { UseSystemPasswordChar = true };
    private readonly TextBox _txtConfirmPassword = new() { UseSystemPasswordChar = true };
    private readonly CheckBox _chkMustChange = new() { Text = "User must change password at next logon", Checked = true };

    // ---- Step 3: OU + Manager ----
    private readonly TextBox _txtOuDisplay = new() { ReadOnly = true, Text = "(not selected)" };
    private readonly TextBox _txtManagerDisplay = new() { ReadOnly = true, Text = "(not selected)" };
    private string? _ouDn;
    private string? _managerDn;
    private string? _managerDisplay;

    // ---- Step 4: Security Groups ----
    private readonly ListBox _lstGroups = new();

    // ---- Step 5: Computer Assignment ----
    private readonly TextBox _txtComputer = new();
    private readonly Label _lblComputerStatus = new() { ForeColor = Color.DarkRed, AutoSize = false };
    /// <summary>Set by CheckComputer when the typed/picked device already has a non-blank
    /// Description (this app's existing "someone's currently assigned to it" signal, same one
    /// TransferTargetDialog's Browse list filters on) - null means the device is free to assign.
    /// Gates a hard confirm in ValidateStep before Next is allowed to proceed, since
    /// UpsertInventoryRow would otherwise silently overwrite whoever currently has it.</summary>
    private InventoryItem? _existingAssignedComputer;
    private bool _computerFound;

    // ---- Step 6: Inventory Details ----
    private readonly Dictionary<string, Control> _inventoryControls = new();

    // ---- Step 7: Review ----
    private readonly TextBox _txtReview = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };

    public OnboardingWizardForm(IAdService adService, InventoryStore store, string upnSuffix, string suUsername, Action<string, AppLogLevel> log, ISecurityAudit? audit = null)
    {
        _adService = adService;
        _store = store;
        _upnSuffix = upnSuffix;
        _suUsername = suUsername;
        _audit = audit ?? NullSecurityAudit.Instance;
        _log = log;

        Icon = AppIcon.Shared;
        Text = "Onboard New User";
        Size = new Size(700, 560);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        _lblStepTitle = new Label { Dock = DockStyle.Top, Height = 30, Font = new Font(Font, FontStyle.Bold), Padding = new Padding(15, 8, 0, 0) };

        var contentPanel = new Panel { Dock = DockStyle.Fill };
        _steps =
        [
            BuildBasicInfoStep(), BuildAccountDetailsStep(), BuildOuManagerStep(), BuildGroupsStep(),
            BuildComputerStep(), BuildInventoryStep(), BuildReviewStep(),
        ];
        foreach (var panel in _steps)
        {
            panel.Dock = DockStyle.Fill;
            panel.Visible = false;
            contentPanel.Controls.Add(panel);
        }

        var navPanel = new Panel { Dock = DockStyle.Bottom, Height = 50 };
        var btnCancel = new Button { Text = "Cancel", Size = new Size(90, 30), Location = new Point(600, 10) };
        _btnNext = new Button { Text = "Next >", Size = new Size(90, 30), Location = new Point(500, 10) };
        _btnBack = new Button { Text = "< Back", Size = new Size(90, 30), Location = new Point(400, 10) };
        btnCancel.Click += (_, _) => Close();
        _btnBack.Click += (_, _) => GoBack();
        _btnNext.Click += (_, _) => GoNext();
        navPanel.Controls.Add(btnCancel);
        navPanel.Controls.Add(_btnNext);
        navPanel.Controls.Add(_btnBack);

        Controls.Add(contentPanel);
        Controls.Add(navPanel);
        Controls.Add(_lblStepTitle);
        AcceptButton = _btnNext;

        ShowStep();
    }

    private void ShowStep()
    {
        foreach (var panel in _steps)
        {
            panel.Visible = false;
        }
        _steps[_currentStep].Visible = true;
        _lblStepTitle.Text = $"Step {_currentStep + 1} of {_steps.Length}: {StepTitles[_currentStep]}";
        _btnBack.Enabled = _currentStep > 0;
        _btnNext.Text = _currentStep == _steps.Length - 1 ? "Finish" : "Next >";
    }

    private void GoBack()
    {
        if (_currentStep > 0)
        {
            _currentStep--;
            ShowStep();
        }
    }

    private void GoNext()
    {
        if (_currentStep == _steps.Length - 1)
        {
            Finish();
            return;
        }
        if (!ValidateCurrentStep())
        {
            return;
        }
        if (_currentStep == 5) // leaving Inventory Details -> build the Review text
        {
            BuildReviewText();
        }
        _currentStep++;
        ShowStep();
    }

    private bool ValidateCurrentStep()
    {
        switch (_currentStep)
        {
            case 0:
                if (string.IsNullOrWhiteSpace(_txtTicket.Text))
                {
                    Warn("Enter a ticket number.");
                    return false;
                }
                if (string.IsNullOrWhiteSpace(_txtFirstName.Text) || string.IsNullOrWhiteSpace(_txtLastName.Text))
                {
                    Warn("Enter both a First Name and Last Name.");
                    return false;
                }
                if (string.IsNullOrWhiteSpace(_txtSam.Text))
                {
                    // Only auto-generate once - if the admin already edited it (including via
                    // Back/Next), don't clobber their edit.
                    Cursor = Cursors.WaitCursor;
                    try
                    {
                        var baseName = AccountNaming.SanitizeBase(_txtLastName.Text.Trim(), _txtFirstName.Text.Trim());
                        _txtSam.Text = AccountNaming.GenerateUnique(baseName, _adService.SamAccountNameExists);
                    }
                    catch (Exception ex)
                    {
                        Warn($"Could not check Active Directory for account name availability: {ex.Message}");
                        return false;
                    }
                    finally
                    {
                        Cursor = Cursors.Default;
                    }
                }
                return true;

            case 1:
                if (string.IsNullOrWhiteSpace(_txtSam.Text))
                {
                    Warn("Enter an account name.");
                    return false;
                }
                if (string.IsNullOrWhiteSpace(_txtPassword.Text))
                {
                    Warn("Enter a temporary password.");
                    return false;
                }
                if (_txtPassword.Text != _txtConfirmPassword.Text)
                {
                    Warn("Passwords do not match.");
                    return false;
                }
                return true;

            case 2:
                if (string.IsNullOrWhiteSpace(_ouDn))
                {
                    Warn("Choose an Organizational Unit.");
                    return false;
                }
                if (string.IsNullOrWhiteSpace(_managerDn))
                {
                    Warn("Choose a Manager.");
                    return false;
                }
                return true;

            case 4:
                if (string.IsNullOrWhiteSpace(_txtComputer.Text))
                {
                    Warn("Enter a computer name.");
                    return false;
                }
                CheckComputer();
                if (_existingAssignedComputer is { } conflict)
                {
                    var assignedTo = !string.IsNullOrWhiteSpace(conflict.FullName) ? conflict.FullName : conflict.Username;
                    var confirm = MessageBox.Show(this,
                        $"'{_txtComputer.Text.Trim()}' is currently assigned to {(string.IsNullOrWhiteSpace(assignedTo) ? "someone" : assignedTo)}.\r\n\r\n" +
                        "Continuing will reassign this device to the new hire and overwrite its current record.\r\n\r\n" +
                        "Only continue if this is an intentional reassignment - otherwise go back and pick a different, available device.",
                        "Device Already Assigned", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                    if (confirm != DialogResult.Yes)
                    {
                        return false;
                    }
                }
                return true;

            default:
                return true;
        }
    }

    private void Warn(string message) => MessageBox.Show(this, message, "Onboard New User", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    // ---- Step builders ----

    private Panel BuildBasicInfoStep()
    {
        var panel = new Panel();
        AddLabeledControl(panel, "Ticket #:", _txtTicket, 30);
        AddLabeledControl(panel, "First Name:", _txtFirstName, 70);
        AddLabeledControl(panel, "Last Name:", _txtLastName, 110);
        AddLabeledControl(panel, "Description:", _txtDescription, 150, width: 350);
        AddLabeledControl(panel, "Office:", _txtOffice, 190, width: 350);

        // Job Title/Department/Company map to AD's "Organization" tab, same as Office above -
        // Francois, 2026-09-16.
        AddLabeledControl(panel, "Job Title:", _txtJobTitle, 230, width: 350);
        AddLabeledControl(panel, "Department:", _txtDepartment, 270, width: 350);
        AddLabeledControl(panel, "Company:", _txtCompany, 310, width: 250);
        _chkChangeOrganization.Location = new Point(420, 307);
        _chkChangeOrganization.Size = new Size(160, 20);
        _chkChangeOrganization.CheckedChanged += (_, _) => _txtCompany.ReadOnly = !_chkChangeOrganization.Checked;
        panel.Controls.Add(_chkChangeOrganization);
        return panel;
    }

    private Panel BuildAccountDetailsStep()
    {
        var panel = new Panel();
        AddLabeledControl(panel, "Account Name:", _txtSam, 30, width: 220);
        _txtSam.TextChanged += (_, _) => _lblUpnValue.Text = $"{_txtSam.Text}@{_upnSuffix}";
        var lblUpnLabel = new Label { Text = "Login (UPN):", Location = new Point(30, 65), Size = new Size(140, 20) };
        _lblUpnValue.Location = new Point(180, 65);
        _lblUpnValue.Size = new Size(350, 20);
        _lblUpnValue.ForeColor = Color.DimGray;
        panel.Controls.Add(lblUpnLabel);
        panel.Controls.Add(_lblUpnValue);
        AddLabeledControl(panel, "Temporary Password:", _txtPassword, 105, width: 220);
        AddLabeledControl(panel, "Confirm Password:", _txtConfirmPassword, 140, width: 220);
        _chkMustChange.Location = new Point(30, 175);
        _chkMustChange.Size = new Size(400, 20);
        panel.Controls.Add(_chkMustChange);
        return panel;
    }

    private Panel BuildOuManagerStep()
    {
        var panel = new Panel();
        AddLabeledControl(panel, "Organizational Unit:", _txtOuDisplay, 30, width: 340);
        var btnBrowseOu = new Button { Text = "Browse...", Location = new Point(500, 27), Size = new Size(100, 26) };
        btnBrowseOu.Click += (_, _) =>
        {
            using var dialog = new OuPickerDialog(_adService);
            if (dialog.ShowDialog(this) == DialogResult.OK && dialog.SelectedDistinguishedName is { } dn)
            {
                _ouDn = dn;
                _txtOuDisplay.Text = dn;
            }
        };
        panel.Controls.Add(btnBrowseOu);

        AddLabeledControl(panel, "Manager:", _txtManagerDisplay, 95, width: 340);
        var btnBrowseManager = new Button { Text = "Browse...", Location = new Point(500, 92), Size = new Size(100, 26) };
        btnBrowseManager.Click += (_, _) =>
        {
            using var dialog = new ManagerPickerDialog(_adService);
            if (dialog.ShowDialog(this) == DialogResult.OK && dialog.Selected is { } picked)
            {
                _managerDn = picked.DistinguishedName;
                _managerDisplay = $"{picked.GivenName} {picked.Surname} ({picked.SamAccountName})";
                _txtManagerDisplay.Text = _managerDisplay;
            }
        };
        panel.Controls.Add(btnBrowseManager);
        return panel;
    }

    private Panel BuildGroupsStep()
    {
        var panel = new Panel();
        var lbl = new Label { Text = "Security Groups:", Location = new Point(30, 15), Size = new Size(140, 20) };
        _lstGroups.Location = new Point(30, 40);
        _lstGroups.Size = new Size(420, 220);
        var btnAdd = new Button { Text = "Add Groups...", Location = new Point(460, 40), Size = new Size(140, 28) };
        var btnRemove = new Button { Text = "Remove Selected", Location = new Point(460, 75), Size = new Size(140, 28) };
        btnAdd.Click += (_, _) =>
        {
            using var dialog = new GroupPickerDialog(_adService, PrivilegedGroupGrantPolicy.IsAllowed(_suUsername));
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                foreach (var name in dialog.SelectedGroupNames)
                {
                    if (!_lstGroups.Items.Contains(name))
                    {
                        _lstGroups.Items.Add(name);
                    }
                }
            }
        };
        btnRemove.Click += (_, _) =>
        {
            foreach (var item in _lstGroups.SelectedItems.Cast<object>().ToList())
            {
                _lstGroups.Items.Remove(item);
            }
        };
        panel.Controls.Add(lbl);
        panel.Controls.Add(_lstGroups);
        panel.Controls.Add(btnAdd);
        panel.Controls.Add(btnRemove);
        return panel;
    }

    private Panel BuildComputerStep()
    {
        var panel = new Panel();
        AddLabeledControl(panel, "Computer Name:", _txtComputer, 30, width: 220);
        var btnCheck = new Button { Text = "Check", Location = new Point(400, 27), Size = new Size(80, 26) };
        btnCheck.Click += (_, _) => CheckComputer();
        // Port of the PowerShell wizard's own Browse button for this step (Show-TransferTargetDialog
        // "Select Computer to Onboard") - missed in the original rewrite of this step; reuses the
        // same "available device" picker Transfer User already uses, rather than building a second
        // one.
        var btnBrowse = new Button { Text = "Browse...", Location = new Point(490, 27), Size = new Size(100, 26) };
        btnBrowse.Click += (_, _) => BrowseComputer();
        _lblComputerStatus.Location = new Point(30, 65);
        _lblComputerStatus.Size = new Size(560, 60);
        panel.Controls.Add(btnCheck);
        panel.Controls.Add(btnBrowse);
        panel.Controls.Add(_lblComputerStatus);
        return panel;
    }

    private void BrowseComputer()
    {
        // No "current device" to exclude here (unlike Transfer, which excludes the device being
        // transferred FROM) - Id -1 never matches a real inventory Id, so nothing gets excluded.
        using var dialog = new TransferTargetDialog(_store.Items, excludeId: -1, "Select Computer to Onboard", OnboardingComputerSourceStatuses);
        if (dialog.ShowDialog(this) == DialogResult.OK && dialog.Selected is { } item)
        {
            _txtComputer.Text = item.DeviceName;
            CheckComputer();
        }
    }

    private void CheckComputer()
    {
        var name = _txtComputer.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            // Only reachable by clicking Check directly with an empty field - the Next-button
            // path already warns and refuses to advance before ever calling this. Matches the
            // PowerShell wizard's own "Enter a computer name to check." message.
            _computerFound = false;
            _existingAssignedComputer = null;
            _lblComputerStatus.ForeColor = Color.DarkRed;
            _lblComputerStatus.Text = "Enter a computer name to check.";
            return;
        }
        Cursor = Cursors.WaitCursor;
        try
        {
            var computer = _adService.GetComputer(name);
            _computerFound = computer is not null;
            var existingRow = _store.Items.FirstOrDefault(i => string.Equals(i.DeviceName.Trim(), name, StringComparison.OrdinalIgnoreCase));
            // Blank Description is this app's existing "nobody's currently assigned to this
            // device" signal (the same one TransferTargetDialog's own available-device list
            // filters on) - a non-blank Description means UpsertInventoryRow would otherwise
            // silently overwrite whoever currently has it with the new hire's info.
            _existingAssignedComputer = existingRow is not null && !string.IsNullOrWhiteSpace(existingRow.Description) ? existingRow : null;

            if (_existingAssignedComputer is { } conflict)
            {
                var assignedTo = !string.IsNullOrWhiteSpace(conflict.FullName) ? conflict.FullName : conflict.Username;
                _lblComputerStatus.ForeColor = Color.Firebrick;
                _lblComputerStatus.Text = $"'{name}' is currently assigned to {(string.IsNullOrWhiteSpace(assignedTo) ? "someone" : assignedTo)} (Description: '{conflict.Description}').\n" +
                    "Continuing will reassign this device away from them and overwrite its record. Pick a different, available device unless this is an intentional reassignment.";
            }
            // Port of the PowerShell wizard's own two-color, two-line status message - tells the
            // admin up front whether Finish will UPDATE an existing inventory row for this device
            // or CREATE a new one, not just whether AD found it.
            else if (_computerFound)
            {
                _lblComputerStatus.ForeColor = Color.DarkGreen;
                _lblComputerStatus.Text = $"'{name}' found in Active Directory. Its Description will be updated.\n" +
                    (existingRow is not null
                        ? $"An existing inventory record for '{name}' will be updated."
                        : $"A new inventory record will be created for '{name}'.");
            }
            else
            {
                _lblComputerStatus.ForeColor = Color.DarkOrange;
                _lblComputerStatus.Text = $"'{name}' was NOT found in Active Directory.\n" +
                    "You can still continue - the AD user and inventory record will be created, but the computer's AD Description won't be updated until it's joined and linked later (e.g. via Match User).";
            }
        }
        catch (Exception ex)
        {
            _computerFound = false;
            _existingAssignedComputer = null;
            _lblComputerStatus.ForeColor = Color.DarkRed;
            _lblComputerStatus.Text = $"Could not check Active Directory: {ex.Message}";
        }
        finally
        {
            Cursor = Cursors.Default;
        }
    }

    private Panel BuildInventoryStep()
    {
        var panel = new Panel { AutoScroll = true };
        var fields = InventorySchema.VisibleFieldDefs.Where(f => !ExcludedFromInventoryStep.Contains(f.Key)).ToList();
        var half = (int)Math.Ceiling(fields.Count / 2.0);
        const int rowHeight = 35;
        const int startY = 15;

        for (var i = 0; i < fields.Count; i++)
        {
            var field = fields[i];
            int labelX, ctrlX, ctrlW, row;
            if (i < half) { labelX = 15; ctrlX = 135; ctrlW = 160; row = i; }
            else { labelX = 330; ctrlX = 450; ctrlW = 160; row = i - half; }
            var y = startY + (row * rowHeight);

            panel.Controls.Add(new Label { Text = $"{field.Label}:", Location = new Point(labelX, y), Size = new Size(115, 20) });

            Control control = field.Type switch
            {
                FieldType.Dropdown => BuildInventoryDropdown(field, ctrlX, y, ctrlW),
                FieldType.DynamicDropdown => BuildInventoryDynamicDropdown(field, ctrlX, y, ctrlW),
                _ => new TextBox { Location = new Point(ctrlX, y - 3), Size = new Size(ctrlW, 20) },
            };
            panel.Controls.Add(control);
            _inventoryControls[field.Key] = control;
        }
        return panel;
    }

    private static ComboBox BuildInventoryDropdown(FieldDefinition field, int x, int y, int width)
    {
        var combo = new ComboBox { Location = new Point(x, y - 3), Size = new Size(width, 20), DropDownStyle = ComboBoxStyle.DropDownList };
        combo.Items.AddRange(field.Options);
        combo.SelectedItem = field.Default;
        return combo;
    }

    private ComboBox BuildInventoryDynamicDropdown(FieldDefinition field, int x, int y, int width)
    {
        var combo = new ComboBox { Location = new Point(x, y - 3), Size = new Size(width, 20), DropDownStyle = ComboBoxStyle.DropDown };
        combo.Items.AddRange(InventorySchema.GetDistinctFieldValues(_store.Items, field.Key).ToArray());
        return combo;
    }

    private Panel BuildReviewStep()
    {
        var panel = new Panel();
        _txtReview.Location = new Point(20, 20);
        _txtReview.Size = new Size(620, 400);
        panel.Controls.Add(_txtReview);
        return panel;
    }

    private void BuildReviewText()
    {
        var groupsText = _lstGroups.Items.Count == 0 ? "(none)" : string.Join("\r\n  ", _lstGroups.Items.Cast<string>());
        _txtReview.Text =
            $"Name:      {_txtFirstName.Text.Trim()} {_txtLastName.Text.Trim()}\r\n" +
            $"Account:   {_txtSam.Text.Trim()}\r\n" +
            $"Login:     {_lblUpnValue.Text}\r\n" +
            $"Email:     {_lblUpnValue.Text}\r\n" +
            $"Must change password at next logon: {_chkMustChange.Checked}\r\n" +
            $"OU:        {_ouDn}\r\n" +
            $"Manager:   {_managerDisplay}\r\n" +
            $"Ticket #:  {_txtTicket.Text.Trim()}\r\n" +
            $"Description: {_txtDescription.Text.Trim()}\r\n" +
            $"Office:    {_txtOffice.Text.Trim()}\r\n" +
            $"Job Title: {_txtJobTitle.Text.Trim()}\r\n" +
            $"Department: {_txtDepartment.Text.Trim()}\r\n" +
            $"Company:   {_txtCompany.Text.Trim()}\r\n" +
            $"Groups:\r\n  {groupsText}\r\n" +
            $"Computer:  {_txtComputer.Text.Trim()} {(_computerFound ? "(found in AD)" : "(NOT found in AD)")}\r\n\r\n" +
            "Review the above, then click Finish to create the account, assign groups, and save the inventory record.";
    }

    private void Finish()
    {
        var request = new OnboardingRequest(
            TicketNumber: _txtTicket.Text.Trim(),
            FirstName: _txtFirstName.Text.Trim(),
            LastName: _txtLastName.Text.Trim(),
            Description: _txtDescription.Text.Trim(),
            Office: _txtOffice.Text.Trim(),
            JobTitle: _txtJobTitle.Text.Trim(),
            Department: _txtDepartment.Text.Trim(),
            Company: _txtCompany.Text.Trim(),
            SamAccountName: _txtSam.Text.Trim(),
            UpnSuffix: _upnSuffix,
            Password: _txtPassword.Text,
            MustChangePasswordAtLogon: _chkMustChange.Checked,
            OrganizationalUnitDn: _ouDn ?? "",
            ManagerDistinguishedName: _managerDn ?? "",
            ManagerDisplay: _managerDisplay ?? "",
            GroupNames: _lstGroups.Items.Cast<string>().ToList(),
            ComputerName: _txtComputer.Text.Trim(),
            ComputerFoundInAd: _computerFound,
            SuUsername: _suUsername);

        Cursor = Cursors.WaitCursor;
        try
        {
            var service = new OnboardingService(_adService, audit: _audit);
            OnboardingResult result;
            try
            {
                result = service.CreateUser(request);
            }
            catch (OnboardingBlockedException ex)
            {
                _log($"Onboarding blocked for '{_txtSam.Text.Trim()}' ({_txtFirstName.Text.Trim()} {_txtLastName.Text.Trim()}): {ex.Message}", AppLogLevel.Warning);
                MessageBox.Show(this, ex.Message, "Onboarding Blocked", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            catch (Exception ex)
            {
                _log($"Onboarding failed while creating AD user '{_txtSam.Text.Trim()}': {ex.Message}", AppLogLevel.Error);
                MessageBox.Show(this, $"Could not create the AD user account: {ex.Message}\r\n\r\nNothing else was changed - fix the issue and try again.", "Onboarding Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            UpsertInventoryRow(request, result);
            _store.Save();

            _log($"Onboarding: created AD user '{result.CreatedUser.SamAccountName}' ({result.FullName}), email/proxyAddress set to '{result.CreatedUser.SamAccountName}@{_upnSuffix}', manager '{_managerDisplay}'.", AppLogLevel.Info);
            foreach (var groupOutcome in result.GroupOutcomes)
            {
                if (groupOutcome.Added)
                {
                    _log($"Onboarding: added '{result.CreatedUser.SamAccountName}' to group '{groupOutcome.GroupName}'.", AppLogLevel.Info);
                }
                else
                {
                    _log($"Onboarding: could NOT add '{result.CreatedUser.SamAccountName}' to group '{groupOutcome.GroupName}': {groupOutcome.Reason}", AppLogLevel.Error);
                }
            }
            _log($"Onboarding complete: {result.FullName} ({result.CreatedUser.SamAccountName}) on '{request.ComputerName}'.", AppLogLevel.Info);

            var groupSummary = string.Join("\r\n", result.GroupOutcomes.Select(g => $"  {g.GroupName}: {(g.Added ? "Added" : g.Reason)}"));
            MessageBox.Show(this,
                $"Onboarding complete for {result.FullName}.\r\n\r\nAccount: {result.CreatedUser.SamAccountName}\r\nComputer: {request.ComputerName}" +
                (groupSummary.Length > 0 ? $"\r\n\r\nGroups:\r\n{groupSummary}" : ""),
                "Onboarding Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
            DialogResult = DialogResult.OK;
            Close();
        }
        finally
        {
            Cursor = Cursors.Default;
        }
    }

    private void UpsertInventoryRow(OnboardingRequest request, OnboardingResult result)
    {
        var computerName = request.ComputerName;
        var existing = _store.Items.FirstOrDefault(i => string.Equals(i.DeviceName.Trim(), computerName, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            existing = new InventoryItem { Id = InventorySchema.GetNextId(_store.Items), DeviceName = computerName };
            _store.Items.Add(existing);
        }
        existing.Status = "Current";
        existing.Username = result.CreatedUser.SamAccountName;
        existing.FirstName = request.FirstName;
        existing.LastName = request.LastName;
        existing.Description = $"{result.FullName}***";
        existing.ADFlagReason = "";
        existing.ADComputerDisabled = "";
        existing.ADComputerNotFound = request.ComputerFoundInAd ? "" : "No matching AD computer object found";
        foreach (var (key, control) in _inventoryControls)
        {
            existing[key] = control switch
            {
                ComboBox { DropDownStyle: ComboBoxStyle.DropDownList } combo => $"{combo.SelectedItem}",
                ComboBox combo => combo.Text.Trim(),
                TextBox textBox => textBox.Text.Trim(),
                _ => "",
            };
        }
    }

    private static void AddLabeledControl(Panel panel, string label, Control control, int y, int width = 250)
    {
        panel.Controls.Add(new Label { Text = label, Location = new Point(30, y), Size = new Size(120, 20) });
        control.Location = new Point(160, y - 3);
        control.Size = new Size(width, 20);
        panel.Controls.Add(control);
    }
}
