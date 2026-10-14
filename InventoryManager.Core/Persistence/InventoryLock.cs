namespace InventoryManager.Core.Persistence;

/// <summary>
/// Direct port of the PowerShell build's Enter-InventoryLock / Exit-InventoryLock. inventory.json
/// is a shared file, potentially open right now by several other copies of this app on other
/// machines - an exclusive CreateNew open of a small separate marker file is the one locking
/// primitive that actually works across machines over a network share. A .NET Mutex is
/// local-machine-only and would do nothing for that case, which is why this exists instead.
/// </summary>
public sealed class InventoryLock
{
    private readonly string _lockFilePath;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _staleAfter;

    public InventoryLock(string lockFilePath, TimeSpan? timeout = null, TimeSpan? staleAfter = null)
    {
        _lockFilePath = lockFilePath;
        _timeout = timeout ?? TimeSpan.FromSeconds(15);
        _staleAfter = staleAfter ?? TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// Blocks (polling) until the lock is acquired or <see cref="_timeout"/> elapses. Returns the
    /// open handle (pass it to <see cref="Release"/>) or null on timeout.
    /// </summary>
    public FileStream? Acquire()
    {
        var deadline = DateTime.UtcNow + _timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var stream = new FileStream(_lockFilePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                try
                {
                    using var writer = new StreamWriter(stream, leaveOpen: true);
                    writer.Write($"{Environment.UserName} @ {Environment.MachineName} @ {DateTime.UtcNow:O}");
                    writer.Flush();
                }
                catch
                {
                    // Diagnostic content only - a failure to write it is never a reason to give up the lock.
                }
                return stream;
            }
            catch (IOException)
            {
                // Someone else holds it, or a crashed process left it behind. Only steal it if
                // it's clearly stale (far older than any real save should ever take) - never on
                // a whim.
                TryStealIfStale();
                Thread.Sleep(200);
            }
        }
        return null;
    }

    private void TryStealIfStale()
    {
        try
        {
            if (File.Exists(_lockFilePath))
            {
                var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(_lockFilePath);
                if (age > _staleAfter)
                {
                    File.Delete(_lockFilePath);
                }
            }
        }
        catch
        {
            // Another session may have just released/deleted it - fine either way, the next
            // Acquire attempt will sort itself out.
        }
    }

    public void Release(FileStream? handle)
    {
        try
        {
            handle?.Dispose();
        }
        catch
        {
            // Already gone - nothing more to do.
        }
        try
        {
            File.Delete(_lockFilePath);
        }
        catch
        {
            // Already gone, or another session's stale-steal beat us to it.
        }
    }
}
