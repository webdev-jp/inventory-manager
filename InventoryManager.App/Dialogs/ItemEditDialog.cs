using InventoryManager.Core;
using InventoryManager.Core.Ad;
using InventoryManager.Core.Models;

namespace InventoryManager.App.Dialogs;

/// <summary>
/// Direct port of the PowerShell build's Show-ItemDialog: builds its fields from
/// InventorySchema.VisibleFieldDefs (two-column layout, same row height/spacing) instead of
/// hardcoding a form per field, so adding a field to the schema is the only change needed.
/// <para>When given an <see cref="IAdService"/>, the Username fills itself in from Active Directory
/// as soon as the name is typed (see <see cref="AutoMatch"/>), with a Change... button to pick a
/// different person - so the right-click Match User is no longer needed for a record being
/// entered or edited here.</para>
/// </summary>
public sealed class ItemEditDialog : Form
{
    private readonly Dictionary<string, Control> _controls = new();
    private readonly IReadOnlyList<InventoryItem> _existingItems;
    private readonly IAdService? _adService;
    private readonly Label _lblMatch = new() { AutoSize = false, Size = new Size(385, 34) };
    private readonly string _initialNameKey;
    private string _lastLookupKey = "";

    public Dictionary<string, string>? Result { get; private set; }

    public ItemEditDialog(string title, IReadOnlyDictionary<string, string>? values, IReadOnlyList<InventoryItem> existingItems, IAdService? adService = null)
    {
        _existingItems = existingItems;
        _adService = adService;
        string Initial(string key) => values is not null && values.TryGetValue(key, out var v) ? v : "";
        _initialNameKey = NameKey(Initial("LastName"), Initial("FirstName"));

        Icon = AppIcon.Shared;
        Text = title;
        Size = new Size(adService is null ? 700 : 740, 440);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        var fields = InventorySchema.VisibleFieldDefs;
        var half = (int)Math.Ceiling(fields.Count / 2.0);
        const int rowHeight = 35;
        const int startY = 15;

        for (var i = 0; i < fields.Count; i++)
        {
            var field = fields[i];
            int labelX, ctrlX, ctrlW, row;
            if (i < half)
            {
                labelX = 15; ctrlX = 135; ctrlW = 160; row = i;
            }
            else
            {
                labelX = 330; ctrlX = 450; ctrlW = 160; row = i - half;
            }
            var y = startY + (row * rowHeight);

            var label = new Label
            {
                Text = $"{field.Label}:",
                Location = new Point(labelX, y),
                Size = new Size(115, 20),
            };
            Controls.Add(label);

            var currentValue = values is not null && values.TryGetValue(field.Key, out var existing)
                ? existing
                : InventorySchema.GetFieldDefault(field);

            Control control = field.Type switch
            {
                FieldType.Dropdown => BuildDropdown(field, currentValue, ctrlX, y, ctrlW),
                FieldType.DynamicDropdown => BuildDynamicDropdown(field, currentValue, ctrlX, y, ctrlW),
                _ => BuildTextBox(field, currentValue, ctrlX, y, ctrlW),
            };
            Controls.Add(control);
            _controls[field.Key] = control;
        }

        var btnY = startY + (half * rowHeight) + 15;
        var btnOk = new Button { Text = "OK", Location = new Point(450, btnY), Size = new Size(90, 28), DialogResult = DialogResult.OK };
        var btnCancel = new Button { Text = "Cancel", Location = new Point(550, btnY), Size = new Size(90, 28), DialogResult = DialogResult.Cancel };
        btnOk.Click += (_, _) => OnOk();
        Controls.Add(btnOk);
        Controls.Add(btnCancel);
        AcceptButton = btnOk;
        CancelButton = btnCancel;

        if (adService is not null)
        {
            SetUpAutoMatch();
        }
    }

    private static string NameKey(string lastName, string firstName) =>
        $"{lastName.Trim()}|{firstName.Trim()}".ToLowerInvariant();

    private TextBox Box(string key) => (TextBox)_controls[key];

    private void SetUpAutoMatch()
    {
        var username = Box("Username");
        _lblMatch.Location = new Point(330, username.Bottom + 4);
        Controls.Add(_lblMatch);

        var btnChange = new Button
        {
            Text = "Change...",
            Location = new Point(username.Right + 5, username.Top - 2),
            Size = new Size(90, 24),
        };
        btnChange.Click += (_, _) => OnChangeClicked();
        Controls.Add(btnChange);

        // Leave (not TextChanged): look up once the person has finished typing a name, not on
        // every keystroke.
        Box("LastName").Leave += (_, _) => AutoMatch();
        Box("FirstName").Leave += (_, _) => AutoMatch();
        Shown += (_, _) => AutoMatch();
    }

    private void SetStatus(string text, bool warn)
    {
        _lblMatch.Text = text;
        _lblMatch.ForeColor = warn ? Color.FromArgb(160, 80, 0) : Color.FromArgb(27, 94, 32);
    }

    /// <summary>Looks the typed name up in AD and, when exactly one person clearly fits, fills in the
    /// Username (and tidies the name to AD's spelling). An unsure match is only described, never
    /// applied, and an existing link is left alone unless the name was actually changed. Cheap to
    /// call repeatedly: it does nothing unless the name differs from the last lookup.</summary>
    private void AutoMatch()
    {
        if (_adService is null)
        {
            return;
        }
        var lastName = Box("LastName").Text.Trim();
        var firstName = Box("FirstName").Text.Trim();
        var key = NameKey(lastName, firstName);
        if (key == _lastLookupKey)
        {
            return;
        }
        _lastLookupKey = key;

        var current = Box("Username").Text.Trim();
        var keep = current.Length > 0 ? $" Still linked to {current}." : "";
        if (lastName.Length == 0)
        {
            SetStatus(current.Length > 0 ? $"Linked to AD user {current}." : "Enter a last name to match this record to an AD user.", warn: current.Length == 0);
            return;
        }
        if (current.Length > 0 && key == _initialNameKey)
        {
            SetStatus($"Linked to AD user {current}.", warn: false);
            return;
        }

        IReadOnlyList<AdUserSummary> candidates;
        Cursor = Cursors.WaitCursor;
        try
        {
            candidates = _adService.SearchUsersByExactSurname(lastName);
        }
        catch (Exception ex)
        {
            SetStatus($"Couldn't check Active Directory: {ex.Message}{keep}", warn: true);
            return;
        }
        finally
        {
            Cursor = Cursors.Default;
        }

        var result = AdUserAutoMatcher.Match(candidates, firstName);
        switch (result.Outcome)
        {
            case AutoMatchOutcome.Matched:
                ApplyUser(result.User!, "Matched to");
                break;
            case AutoMatchOutcome.PossibleOnly:
                SetStatus($"Only {result.User!.GivenName} {result.User.Surname} ({result.User.SamAccountName}) has that last name, but the first name differs.{keep} Click Change... to use them or pick someone else.", warn: true);
                break;
            case AutoMatchOutcome.Several:
                SetStatus($"{result.CandidateCount} AD users could match.{keep} Click Change... to choose.", warn: true);
                break;
            default:
                SetStatus($"No AD user has the last name '{lastName}'.{keep} Click Change... to search.", warn: true);
                break;
        }
    }

    private void ApplyUser(AdUserSummary user, string how)
    {
        Box("Username").Text = user.SamAccountName;
        Box("FirstName").Text = user.GivenName;
        Box("LastName").Text = user.Surname;
        // The names now equal AD's - don't look the same person up again on the next Leave.
        _lastLookupKey = NameKey(user.Surname, user.GivenName);
        SetStatus($"{how} {user.GivenName} {user.Surname} ({user.SamAccountName}).", warn: false);
    }

    private void OnChangeClicked()
    {
        if (_adService is null)
        {
            return;
        }
        var lastName = Box("LastName").Text.Trim();
        var firstName = Box("FirstName").Text.Trim();
        var term = lastName.Length > 0 ? lastName : firstName;
        using var dialog = new AdUserSelectDialog(_adService, term, $"{firstName} {lastName}".Trim());
        if (dialog.ShowDialog(this) == DialogResult.OK && dialog.Result is { } picked)
        {
            ApplyUser(picked, "Matched to");
        }
    }

    private ComboBox BuildDropdown(FieldDefinition field, string currentValue, int x, int y, int width)
    {
        var combo = new ComboBox
        {
            Location = new Point(x, y - 3),
            Size = new Size(width, 20),
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        combo.Items.AddRange(field.Options);
        combo.SelectedItem = combo.Items.Contains(currentValue) ? currentValue : field.Default;
        return combo;
    }

    private ComboBox BuildDynamicDropdown(FieldDefinition field, string currentValue, int x, int y, int width)
    {
        // Editable combo (DropDown, not DropDownList) - pick a value already used elsewhere in
        // the inventory, or just type a new one if this is genuinely a new value. Read back via
        // .Text, same as a plain textbox.
        var combo = new ComboBox
        {
            Location = new Point(x, y - 3),
            Size = new Size(width, 20),
            DropDownStyle = ComboBoxStyle.DropDown,
        };
        combo.Items.AddRange(InventorySchema.GetDistinctFieldValues(_existingItems, field.Key).ToArray());
        combo.Text = currentValue;
        return combo;
    }

    private static TextBox BuildTextBox(FieldDefinition field, string currentValue, int x, int y, int width)
    {
        var textBox = new TextBox
        {
            Location = new Point(x, y - 3),
            Size = new Size(width, 20),
            Text = currentValue,
        };
        if (field.ReadOnly)
        {
            textBox.ReadOnly = true;
            textBox.TabStop = false;
            textBox.BackColor = SystemColors.Control;
        }
        return textBox;
    }

    private void OnOk()
    {
        // Pressing Enter in a name box (or clicking OK straight away) doesn't always fire Leave
        // first - make sure the name that's about to be saved has been matched.
        AutoMatch();
        var result = new Dictionary<string, string>();
        foreach (var field in InventorySchema.VisibleFieldDefs)
        {
            var control = _controls[field.Key];
            result[field.Key] = control switch
            {
                ComboBox { DropDownStyle: ComboBoxStyle.DropDownList } combo => $"{combo.SelectedItem}",
                ComboBox combo => combo.Text.Trim(),
                TextBox textBox => textBox.Text.Trim(),
                _ => "",
            };
        }
        Result = result;
    }
}
