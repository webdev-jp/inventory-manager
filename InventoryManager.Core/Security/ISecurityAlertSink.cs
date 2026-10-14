namespace InventoryManager.Core.Security;

/// <summary>
/// Where a raised alert goes. Deliberately tiny so a Teams/webhook or email sink can be dropped in
/// later without touching callers. <see cref="SecurityEventLog"/> raises to its sink for any event
/// at or above a configured severity (default Warning), so the operator finds out about a refused
/// privileged grant or a tripped honeytoken without having to go read the log.
/// </summary>
public interface ISecurityAlertSink
{
    void Raise(SecurityEvent securityEvent);
}

public sealed class NullAlertSink : ISecurityAlertSink
{
    public static readonly NullAlertSink Instance = new();
    public void Raise(SecurityEvent securityEvent) { }
}

/// <summary>
/// Appends a one-line human-readable alert to a plain text file (separate from the JSONL ledger,
/// so it can point at a location a human or a mail-enabled scheduled task watches). Best-effort:
/// an alerting failure must never bubble up into the operation that triggered it.
/// </summary>
public sealed class FileAlertSink(string alertFilePath) : ISecurityAlertSink
{
    public void Raise(SecurityEvent e)
    {
        try
        {
            var line = $"[{e.TimestampUtc}] {e.Severity.ToString().ToUpperInvariant()} {e.Type} | actor={e.Actor} machine={e.Machine} target=\"{e.Target}\" | {e.Detail}{Environment.NewLine}";
            File.AppendAllText(alertFilePath, line);
        }
        catch
        {
            // Alerting is best-effort - never let it break the caller.
        }
    }
}

/// <summary>Fan-out to several sinks (e.g. a file plus a future webhook). One sink throwing never
/// stops the others.</summary>
public sealed class CompositeAlertSink(params ISecurityAlertSink[] sinks) : ISecurityAlertSink
{
    public void Raise(SecurityEvent e)
    {
        foreach (var sink in sinks)
        {
            try { sink.Raise(e); } catch { /* isolate one bad sink */ }
        }
    }
}
