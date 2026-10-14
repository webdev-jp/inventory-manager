using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace InventoryManager.Core.Security;

/// <summary>
/// Tamper-evident hash chain over the security-event log (a lightweight, dependency-free
/// blockchain-style ledger). Each entry's Hash = SHA-256(canonical(event) + prevHash), and the
/// first entry chains off <see cref="GenesisHash"/>. Because every hash covers the one before it,
/// silently editing or deleting any entry - even by someone who can write the file - breaks the
/// chain from that point on, and <see cref="Verify"/> reports exactly where.
///
/// This makes the log tamper-EVIDENT, not tamper-PROOF: an attacker with write access can still
/// rewrite the whole file from the break onward to reforge a consistent chain. Real prevention
/// needs the file to be append-only at the OS level (restrictive ACLs) or shipped off-box to a
/// SIEM/syslog the operator can't reach - see the deployment notes. The chain's job is to make
/// after-the-fact deletion of a single incriminating line detectable.
/// </summary>
public static class SecurityEventChain
{
    /// <summary>The prevHash the very first entry chains off - 64 hex zeroes.</summary>
    public static readonly string GenesisHash = new('0', 64);

    /// <summary>Deterministic SHA-256 (lowercase hex) over the event's fields plus the previous
    /// hash. Uses a JSON array of the fields in a fixed order as the canonical form, so a value
    /// containing a delimiter can never collide with a different field layout.</summary>
    public static string ComputeHash(string prevHash, SecurityEvent e)
    {
        var canonical = JsonSerializer.Serialize(new[]
        {
            e.TimestampUtc,
            e.Actor,
            e.Machine,
            e.Type.ToString(),
            e.Severity.ToString(),
            e.Target,
            e.Detail,
            prevHash,
        });
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>Recomputes every hash and checks every link. Returns a result whose
    /// <see cref="ChainVerificationResult.IsIntact"/> is true only if all entries chain cleanly
    /// from the genesis; otherwise <see cref="ChainVerificationResult.FirstBrokenIndex"/> is the
    /// 0-based index of the first entry that doesn't verify.</summary>
    public static ChainVerificationResult Verify(IReadOnlyList<SecurityLogEntry> entries)
    {
        for (var i = 0; i < entries.Count; i++)
        {
            var expectedPrev = i == 0 ? GenesisHash : entries[i - 1].Hash;
            if (!string.Equals(entries[i].PrevHash, expectedPrev, StringComparison.Ordinal))
            {
                return new ChainVerificationResult(false, i, $"Entry {i} prevHash does not match entry {i - 1}'s hash - an entry was removed, reordered, or inserted.");
            }
            var recomputed = ComputeHash(entries[i].PrevHash, entries[i].Event);
            if (!string.Equals(recomputed, entries[i].Hash, StringComparison.Ordinal))
            {
                return new ChainVerificationResult(false, i, $"Entry {i} contents were altered after it was written (hash mismatch).");
            }
        }
        return new ChainVerificationResult(true, -1, "Chain intact.");
    }
}

public sealed record ChainVerificationResult(bool IsIntact, int FirstBrokenIndex, string Message);
