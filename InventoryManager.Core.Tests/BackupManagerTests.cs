using InventoryManager.Core.Persistence;

namespace InventoryManager.Core.Tests;

public class BackupManagerTests : IDisposable
{
    private readonly string _backupDir;
    private readonly string _dataFile;

    public BackupManagerTests()
    {
        _backupDir = Path.Combine(Path.GetTempPath(), $"backuptest_{Guid.NewGuid():N}");
        _dataFile = Path.Combine(Path.GetTempPath(), $"backuptest_data_{Guid.NewGuid():N}.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_backupDir))
        {
            Directory.Delete(_backupDir, recursive: true);
        }
        if (File.Exists(_dataFile))
        {
            File.Delete(_dataFile);
        }
    }

    [Fact]
    public void CreateSnapshot_WritesFileAndReturnsPath()
    {
        var manager = new BackupManager(_backupDir);
        var path = manager.CreateSnapshot("[]");
        Assert.True(File.Exists(path));
        Assert.Equal("[]", File.ReadAllText(path));
    }

    [Fact]
    public void CreateSnapshot_SamePathCollision_AppendsSuffix()
    {
        var manager = new BackupManager(_backupDir);
        Directory.CreateDirectory(_backupDir);
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        File.WriteAllText(Path.Combine(_backupDir, $"inventory_{timestamp}.json"), "existing");

        var path = manager.CreateSnapshot("new content");

        Assert.Contains("_1.json", path);
        Assert.Equal("new content", File.ReadAllText(path));
    }

    [Fact]
    public void PruneOldBackups_KeepsOnlyMaxBackupsNewest()
    {
        var manager = new BackupManager(_backupDir, maxBackups: 2);
        Directory.CreateDirectory(_backupDir);
        for (var i = 0; i < 4; i++)
        {
            var path = Path.Combine(_backupDir, $"inventory_2026010{i}_000000.json");
            File.WriteAllText(path, $"content {i}");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(i));
        }

        manager.PruneOldBackups();

        var remaining = manager.ListBackups();
        Assert.Equal(2, remaining.Count);
        Assert.Equal("content 3", File.ReadAllText(remaining[0].FullName));
        Assert.Equal("content 2", File.ReadAllText(remaining[1].FullName));
    }

    [Fact]
    public void GetLatestBackupFile_NoBackupDir_ReturnsNull()
    {
        var manager = new BackupManager(_backupDir);
        Assert.Null(manager.GetLatestBackupFile());
    }

    [Fact]
    public void RunPeriodicCheck_NoExistingBackup_CreatesOne()
    {
        File.WriteAllText(_dataFile, "[\"row1\"]");
        var manager = new BackupManager(_backupDir);

        var error = manager.RunPeriodicCheck(_dataFile);

        Assert.Null(error);
        var latest = manager.GetLatestBackupFile();
        Assert.NotNull(latest);
        Assert.Equal("[\"row1\"]", File.ReadAllText(latest!.FullName));
    }

    [Fact]
    public void RunPeriodicCheck_UnchangedContent_DoesNotCreateSecondSnapshot()
    {
        File.WriteAllText(_dataFile, "[\"row1\"]");
        var manager = new BackupManager(_backupDir);
        manager.RunPeriodicCheck(_dataFile);
        Assert.Single(manager.ListBackups());

        manager.RunPeriodicCheck(_dataFile);

        Assert.Single(manager.ListBackups());
    }

    [Fact]
    public void RunPeriodicCheck_ChangedContent_CreatesNewSnapshot()
    {
        File.WriteAllText(_dataFile, "[\"row1\"]");
        var manager = new BackupManager(_backupDir);
        manager.RunPeriodicCheck(_dataFile);

        File.WriteAllText(_dataFile, "[\"row1\",\"row2\"]");
        Thread.Sleep(1100); // filenames are second-granularity - force a distinct timestamp
        manager.RunPeriodicCheck(_dataFile);

        Assert.Equal(2, manager.ListBackups().Count);
    }

    [Fact]
    public void RunPeriodicCheck_NoDataFile_ReturnsNullAndCreatesNothing()
    {
        var manager = new BackupManager(_backupDir);
        var error = manager.RunPeriodicCheck(_dataFile);
        Assert.Null(error);
        Assert.False(Directory.Exists(_backupDir));
    }
}
