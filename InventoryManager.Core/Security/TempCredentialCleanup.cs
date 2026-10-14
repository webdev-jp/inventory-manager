namespace InventoryManager.Core.Security;

/// <summary>
/// Decides which leftover offboarding credential-handoff files are stale enough to delete.
///
/// Background: offboarding exports the SU account's credentials to a per-use temp file
/// (DPAPI-encrypted, so only THIS Windows user can read it) for the external offboarding script to
/// consume. That file must not outlive the offboarding session - while it exists, any process
/// running as the same user could Import-Clixml it and recover the SU password. The app deletes it
/// as soon as the offboarding process exits, but a crash, an AllSigned block that stops the script
/// before its own cleanup, or a hard power-off can still orphan one. This sweep, run at startup,
/// mops up anything a prior run left behind.
///
/// Pure decision logic (no I/O) so it's unit-testable; the caller does the globbing and deletion.
/// </summary>
public static class TempCredentialCleanup
{
    /// <summary>Filename prefix + search glob shared by the writer and the sweeper, so they can
    /// never drift apart. The <c>*</c> stands in for the per-file GUID.</summary>
    public const string OffboardCredentialPrefix = "invmgr-offboard-cred-";
    public const string OffboardCredentialSearchPattern = "invmgr-offboard-cred-*.xml";

    /// <summary>
    /// Returns the paths whose age (<paramref name="nowUtc"/> minus last-write time) exceeds
    /// <paramref name="maxAge"/>. Files younger than that are left alone so a concurrent second
    /// instance of the same user, mid-offboard, doesn't have its in-flight handoff file yanked out
    /// from under it. Default caller uses a 1-hour cutoff - orders of magnitude longer than a real
    /// credential handoff, which lasts only until the child process reads the file.
    /// </summary>
    public static IReadOnlyList<string> StaleFiles(
        IEnumerable<(string Path, DateTime LastWriteUtc)> files, DateTime nowUtc, TimeSpan maxAge) =>
        files.Where(f => nowUtc - f.LastWriteUtc > maxAge).Select(f => f.Path).ToList();
}
