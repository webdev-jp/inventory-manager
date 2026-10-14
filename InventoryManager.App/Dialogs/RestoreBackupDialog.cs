using InventoryManager.Core.Persistence;

namespace InventoryManager.App.Dialogs;

/// <summary>
/// Port of the PowerShell build's Show-RestoreBackupDialog: pick a point-in-time snapshot and
/// replace the current inventory with it. The current data is backed up first (via the same
/// BackupManager the periodic timer uses) before the swap, so a restore itself can never lose
/// anything - matching the PS build's own stated guarantee.
/// </summary>
public sealed class RestoreBackupDialog : Form
{
    private readonly BackupManager _backupManager;
    private readonly InventoryStore _store;
    private readonly IReadOnlyList<FileInfo> _backups;
    private readonly ListBox _listBox = new() { Font = new Font("Consolas", 9) };

    public bool Restored { get; private set; }

    public RestoreBackupDialog(BackupManager backupManager, InventoryStore store, int maxBackups)
    {
        _backupManager = backupManager;
        _store = store;
        _backups = backupManager.ListBackups();

        Icon = AppIcon.Shared;
        Text = "Restore from Backup";
        Size = new Size(440, 440);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        var lbl = new Label
        {
            Text = $"A backup is checked every 5 minutes and only updated if something changed (last {maxBackups} kept). Pick a point in time to restore to - your current data is backed up first, so this itself can't lose anything.",
            Location = new Point(10, 10),
            Size = new Size(410, 65),
        };
        Controls.Add(lbl);

        _listBox.Location = new Point(10, 80);
        _listBox.Size = new Size(410, 280);
        foreach (var backup in _backups)
        {
            _listBox.Items.Add(backup.LastWriteTime.ToString("yyyy-MM-dd  HH:mm:ss"));
        }
        if (_listBox.Items.Count > 0)
        {
            _listBox.SelectedIndex = 0;
        }
        Controls.Add(_listBox);

        var btnRestore = new Button { Text = "Restore", Location = new Point(230, 370), Size = new Size(90, 28), DialogResult = DialogResult.OK };
        var btnCancel = new Button { Text = "Cancel", Location = new Point(320, 370), Size = new Size(90, 28), DialogResult = DialogResult.Cancel };
        btnRestore.Click += (_, _) => DoRestore();
        Controls.Add(btnRestore);
        Controls.Add(btnCancel);
        AcceptButton = btnRestore;
        CancelButton = btnCancel;
    }

    private void DoRestore()
    {
        if (_listBox.SelectedIndex < 0)
        {
            return;
        }
        var selected = _backups[_listBox.SelectedIndex];
        var confirm = MessageBox.Show(this,
            $"Replace the current inventory with the backup from {selected.LastWriteTime}?\r\n\r\nYour current data will be backed up first, so you can always undo this.",
            "Confirm Restore", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes)
        {
            return;
        }

        // Read the chosen backup's content into memory BEFORE snapshotting/pruning runs, so a
        // prune triggered by the pre-restore backup can never delete the very file we're about
        // to restore out from under us.
        var restoreContent = File.ReadAllText(selected.FullName);
        _backupManager.RunPeriodicCheck(_store.DataFilePath);

        var result = _store.RestoreFromContent(restoreContent);
        if (result == InventorySaveResult.LockTimedOut)
        {
            MessageBox.Show(this, "Could not restore - the inventory file has been locked by another user for too long. Try again in a moment.", "Restore Locked", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Restored = true;
        MessageBox.Show(this, "Inventory restored.", "Restore Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
        DialogResult = DialogResult.OK;
        Close();
    }
}
