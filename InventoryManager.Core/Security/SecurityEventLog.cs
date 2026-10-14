using System.Text.Json;
using System.Text.Json.Nodes;
using InventoryManager.Core.Persistence;

namespace InventoryManager.Core.Security;

/// <summary>
/// Append-only, tamper-evident security ledger. One JSON object per line (JSONL) so it stays
/// human-inspectable and grep-able; each line carries the previous line's hash and its own, so the
/// whole file is a hash chain (<see cref="SecurityEventChain"/>). Appends are serialized with the
/// same cross-machine <see cref="InventoryLock"/> the inventory file uses, so two operators on two
/// machines can't interleave and fork the chain.
///
/// Implements <see cref="ISecurityAudit"/>, so Core services just call Record(...). Events at or
/// above <see cref="_alertThreshold"/> (default Warning) are also pushed to the alert sink.
///
/// Deployment note: put this file where ordinary operators can APPEND but not REWRITE (or ship it
/// to a SIEM). The chain detects tampering; only ACLs prevent it.
/// </summary>
public sealed class SecurityEventLog : ISecurityAudit, ISecurityLogReader
{
    private readonly string _path;
    private readonly ISecurityAlertSink _alertSink;
    private readonly SecuritySeverity _alertThreshold;
    private readonly InventoryLock _lock;
    private readonly object _inProcGate = new();

    public SecurityEventLog(
        string path,
        ISecurityAlertSink? alertSink = null,
        SecuritySeverity alertThreshold = SecuritySeverity.Warning)
    {
        _path = path;
        _alertSink = alertSink ?? NullAlertSink.Instance;
        _alertThreshold = alertThreshold;
        _lock = new InventoryLock(path + ".lock");
    }

    public SecurityEvent Record(SecurityEventType type, SecuritySeverity severity, string actor, string target, string detail)
    {
        var e = new SecurityEvent(
            DateTime.UtcNow.ToString("o"),
            string.IsNullOrWhiteSpace(actor) ? "(unknown)" : actor,
            Environment.MachineName,
            type,
            severity,
            target ?? "",
            detail ?? "");

        try
        {
            Append(e);
        }
        catch
        {
            // Auditing must never be the thing that blocks a legitimate operation. A write failure
            // is swallowed here; the alert below still fires so the event isn't lost entirely.
        }

        if (severity >= _alertThreshold)
        {
            _alertSink.Raise(e);
        }
        return e;
    }

    private void Append(SecurityEvent e)
    {
        lock (_inProcGate)
        {
            var handle = _lock.Acquire();
            try
            {
                var prevHash = ReadLastHash();
                var hash = SecurityEventChain.ComputeHash(prevHash, e);
                var line = Serialize(new SecurityLogEntry(e, prevHash, hash));
                File.AppendAllText(_path, line + "\n");
            }
            finally
            {
                _lock.Release(handle);
            }
        }
    }

    private string ReadLastHash()
    {
        if (!File.Exists(_path))
        {
            return SecurityEventChain.GenesisHash;
        }
        // The last PARSEABLE entry's hash is the chain head. A corrupt/truncated tail is left for
        // Verify() to flag; we still chain forward off the last good entry rather than refuse.
        string? lastHash = null;
        foreach (var rawLine in File.ReadLines(_path))
        {
            if (string.IsNullOrWhiteSpace(rawLine))
            {
                continue;
            }
            if (TryParse(rawLine, out var entry))
            {
                lastHash = entry.Hash;
            }
        }
        return lastHash ?? SecurityEventChain.GenesisHash;
    }

    /// <summary>Every entry currently on disk, in order. Unparseable lines are skipped (see
    /// <see cref="Verify"/> to detect them).</summary>
    public IReadOnlyList<SecurityLogEntry> ReadAll()
    {
        var entries = new List<SecurityLogEntry>();
        if (!File.Exists(_path))
        {
            return entries;
        }
        foreach (var rawLine in File.ReadLines(_path))
        {
            if (!string.IsNullOrWhiteSpace(rawLine) && TryParse(rawLine, out var entry))
            {
                entries.Add(entry);
            }
        }
        return entries;
    }

    /// <summary>Re-reads the file and verifies the whole chain. A line that fails to parse is
    /// itself treated as a break (something rewrote the file badly).</summary>
    public ChainVerificationResult Verify()
    {
        var entries = new List<SecurityLogEntry>();
        if (!File.Exists(_path))
        {
            return new ChainVerificationResult(true, -1, "No log file yet - nothing to verify.");
        }
        var index = 0;
        foreach (var rawLine in File.ReadLines(_path))
        {
            if (string.IsNullOrWhiteSpace(rawLine))
            {
                continue;
            }
            if (!TryParse(rawLine, out var entry))
            {
                return new ChainVerificationResult(false, index, $"Line {index} is not a valid log entry - the file was corrupted or hand-edited.");
            }
            entries.Add(entry);
            index++;
        }
        return SecurityEventChain.Verify(entries);
    }

    private static string Serialize(SecurityLogEntry entry)
    {
        var obj = new JsonObject
        {
            ["ts"] = entry.Event.TimestampUtc,
            ["actor"] = entry.Event.Actor,
            ["machine"] = entry.Event.Machine,
            ["type"] = entry.Event.Type.ToString(),
            ["severity"] = entry.Event.Severity.ToString(),
            ["target"] = entry.Event.Target,
            ["detail"] = entry.Event.Detail,
            ["prev"] = entry.PrevHash,
            ["hash"] = entry.Hash,
        };
        return obj.ToJsonString();
    }

    private static bool TryParse(string line, out SecurityLogEntry entry)
    {
        entry = null!;
        try
        {
            if (JsonNode.Parse(line) is not JsonObject o)
            {
                return false;
            }
            string S(string k) => o.TryGetPropertyValue(k, out var n) && n is not null ? n.GetValue<string>() : "";
            if (!Enum.TryParse<SecurityEventType>(S("type"), out var type) ||
                !Enum.TryParse<SecuritySeverity>(S("severity"), out var sev))
            {
                return false;
            }
            var e = new SecurityEvent(S("ts"), S("actor"), S("machine"), type, sev, S("target"), S("detail"));
            entry = new SecurityLogEntry(e, S("prev"), S("hash"));
            return true;
        }
        catch
        {
            return false;
        }
    }
}
