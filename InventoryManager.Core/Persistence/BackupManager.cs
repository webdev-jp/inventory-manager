namespace InventoryManager.Core.Persistence;

/// <summary>
/// Direct port of the PowerShell build's New-BackupSnapshot / Prune-OldBackups /
/// Get-LatestBackupFile / Invoke-PeriodicBackupCheck. Runs on a timer, not on every edit - if the
/// data file's content hasn't changed since the last snapshot, the existing snapshot file is just
/// refreshed in place (touched) instead of piling up an identical duplicate; if it has changed, a
/// new rolling snapshot is taken, capped at <see cref="_maxBackups"/> (oldest dropped first).
/// </summary>
public sealed class BackupManager(string backupDirectory, int maxBackups = 5)
{
    public FileInfo? GetLatestBackupFile()
    {
        if (!Directory.Exists(backupDirectory))
        {
            return null;
        }
        return new DirectoryInfo(backupDirectory)
            .GetFiles("inventory_*.json")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();
    }

    public IReadOnlyList<FileInfo> ListBackups()
    {
        if (!Directory.Exists(backupDirectory))
        {
            return [];
        }
        return new DirectoryInfo(backupDirectory)
            .GetFiles("inventory_*.json")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ToList();
    }

    public string CreateSnapshot(string content)
    {
        Directory.CreateDirectory(backupDirectory);
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var backupPath = Path.Combine(backupDirectory, $"inventory_{timestamp}.json");
        var suffix = 1;
        while (File.Exists(backupPath))
        {
            backupPath = Path.Combine(backupDirectory, $"inventory_{timestamp}_{suffix}.json");
            suffix++;
        }
        File.WriteAllText(backupPath, content);
        PruneOldBackups();
        return backupPath;
    }

    public void PruneOldBackups()
    {
        if (!Directory.Exists(backupDirectory))
        {
            return;
        }
        var backups = new DirectoryInfo(backupDirectory)
            .GetFiles("inventory_*.json")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ToList();
        foreach (var stale in backups.Skip(maxBackups))
        {
            try
            {
                stale.Delete();
            }
            catch
            {
                // Another session may already be pruning the same file - fine either way.
            }
        }
    }

    /// <summary>Returns null on success, or an error message the caller should surface (matching
    /// the PowerShell build's own "Backup Warning" messagebox on failure) - a backup failure must
    /// never crash or block the app, only be reported.</summary>
    public string? RunPeriodicCheck(string dataFilePath)
    {
        if (!File.Exists(dataFilePath))
        {
            return null;
        }
        try
        {
            var currentContent = File.ReadAllText(dataFilePath);
            var latest = GetLatestBackupFile();
            if (latest is null)
            {
                CreateSnapshot(currentContent);
                return null;
            }
            var latestContent = File.ReadAllText(latest.FullName);
            if (currentContent == latestContent)
            {
                File.WriteAllText(latest.FullName, currentContent);
            }
            else
            {
                CreateSnapshot(currentContent);
            }
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
