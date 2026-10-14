namespace InventoryManager.App;

/// <summary>
/// Loads the app's icon once, from the running exe's own embedded resource (the same one the
/// ApplicationIcon MSBuild property puts there), and shares it across every Form. Needed because
/// ApplicationIcon only sets the FILE's icon (what Explorer/a taskbar shortcut shows before
/// launch) - it does NOT automatically become a running window's title-bar/taskbar icon. In
/// modern .NET WinForms, a Form that never sets its own Icon shows none at all (confirmed live via
/// WM_GETICON returning a null handle, 2026-09-16) - unlike older .NET Framework builds' generic
/// fallback icon, so every Form here needs this set explicitly.
/// </summary>
internal static class AppIcon
{
    public static readonly Icon? Shared = TryLoad();

    private static Icon? TryLoad()
    {
        try
        {
            return Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        }
        catch
        {
            // Never let a missing/unreadable icon resource stop the app from launching.
            return null;
        }
    }
}
