namespace InventoryManager.Core.Logging;

/// <summary>
/// Port of the PowerShell build's Write-AppLog file-writing half: one append-only plain-text file
/// per day (InventoryManager_yyyy-MM-dd.log) in the given log directory. .NET's own file APIs
/// don't share Windows PowerShell 5.1's Add-Content caching bug (the reason the PS build opened a
/// raw FileStream itself instead), so a plain File.AppendAllText is enough here. The App layer
/// also appends each line to an in-memory/on-screen list (once the Activity Log panel exists) -
/// this class only ever owns the persistent file half, matching Write-AppLog's own split.
/// </summary>
public sealed class AppLogWriter(string logDirectory)
{
    /// <summary>The directory this writer appends to - exposed so MainForm's shared-log tailer
    /// (see TailSharedActivityLog) can compute the same day-named file path without duplicating the
    /// naming convention below.</summary>
    public string LogDirectory { get; } = logDirectory;

    public void Write(string message, AppLogLevel level = AppLogLevel.Info, string who = "")
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            var path = Path.Combine(LogDirectory, $"InventoryManager_{DateTime.Now:yyyy-MM-dd}.log");
            File.AppendAllText(path, AppLogFormatter.FormatFileLine(DateTime.Now, level, message, who));
        }
        catch
        {
            // Logging failures must never crash the app - matches the PS source's own try/catch.
        }
    }
}
