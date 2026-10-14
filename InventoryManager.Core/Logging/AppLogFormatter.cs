namespace InventoryManager.Core.Logging;

public enum AppLogLevel
{
    Info,
    Warning,
    Error,
}

/// <summary>Direct port of the PowerShell build's Write-AppLog line formatting - the short
/// in-app-list form and the longer timestamped file-line form.
/// <para><b>2026-09-23:</b> both forms gained an optional <c>who</c> field (the authenticated SU
/// username the line belongs to) - Francois asked why another admin's login never showed up in his
/// own Activity Log even though the log file itself has been shared since the INVMGR_DATA_DIR fix.
/// Root cause: the on-screen panel only ever displayed this session's own in-memory Log() calls,
/// never anything another session appended to the same shared file - and even a live tail (see
/// MainForm's TailSharedActivityLog) would have had no way to tell "my line" from "someone else's
/// line" for two admins who each performed similar actions around the same second, since the file
/// previously carried no identity field at all. <c>who</c> is empty for anything logged before this
/// change shipped, or from any call site that doesn't supply one - <see cref="TryParseFileLine"/>
/// handles both the old 4-field (no who) and new 5-field (with who) shapes.</para></summary>
public static class AppLogFormatter
{
    public static string LevelPrefix(AppLogLevel level) => level switch
    {
        AppLogLevel.Error => "ERROR",
        AppLogLevel.Warning => "WARN",
        _ => "INFO",
    };

    public static string FormatDisplayLine(DateTime timestamp, AppLogLevel level, string message, string who = "") =>
        string.IsNullOrEmpty(who)
            ? $"[{timestamp:HH:mm:ss}] {LevelPrefix(level)}: {message}"
            : $"[{timestamp:HH:mm:ss}] {who} {LevelPrefix(level)}: {message}";

    public static string FormatFileLine(DateTime timestamp, AppLogLevel level, string message, string who = "") =>
        string.IsNullOrEmpty(who)
            ? $"{timestamp:yyyy-MM-dd HH:mm:ss} {LevelPrefix(level)} {message}\r\n"
            : $"{timestamp:yyyy-MM-dd HH:mm:ss} {who} {LevelPrefix(level)} {message}\r\n";

    /// <summary>Parses one line back out of the persistent file format, for tailing the shared log
    /// file to pick up other sessions' lines (see MainForm.TailSharedActivityLog). Tries the newer
    /// 5-field "date time who LEVEL message" shape first; falls back to the original 4-field "date
    /// time LEVEL message" shape (who comes back "") for anything written before this field
    /// existed, or from a call site that never supplied one. Disambiguated by checking whether the
    /// 4th space-separated token is a real level word, not just by field count, since a plain
    /// message can itself contain any number of spaces.</summary>
    public static bool TryParseFileLine(string line, out DateTime timestamp, out AppLogLevel level, out string who, out string message)
    {
        timestamp = default;
        level = AppLogLevel.Info;
        who = "";
        message = "";

        var withWho = line.Split(' ', 5);
        if (withWho.Length == 5 && DateTime.TryParse($"{withWho[0]} {withWho[1]}", out var tsWithWho) && TryParseLevel(withWho[3], out var levelWithWho))
        {
            timestamp = tsWithWho;
            who = withWho[2];
            level = levelWithWho;
            message = withWho[4];
            return true;
        }

        var withoutWho = line.Split(' ', 4);
        if (withoutWho.Length == 4 && DateTime.TryParse($"{withoutWho[0]} {withoutWho[1]}", out var tsWithoutWho) && TryParseLevel(withoutWho[2], out var levelWithoutWho))
        {
            timestamp = tsWithoutWho;
            level = levelWithoutWho;
            message = withoutWho[3];
            return true;
        }

        return false;
    }

    private static bool TryParseLevel(string word, out AppLogLevel level)
    {
        switch (word)
        {
            case "INFO": level = AppLogLevel.Info; return true;
            case "WARN": level = AppLogLevel.Warning; return true;
            case "ERROR": level = AppLogLevel.Error; return true;
            default: level = AppLogLevel.Info; return false;
        }
    }
}
