namespace InventoryManager.Core.Security;

/// <summary>
/// The seam the Core decision services (Onboarding etc.) write security events through, so they
/// stay unit-testable and file-I/O-free - the real implementation is <see cref="SecurityEventLog"/>,
/// tests use an in-memory recorder, and <see cref="NullSecurityAudit"/> is the safe default when
/// no log is wired up (e.g. old call sites, or if log init failed - auditing must never be the
/// thing that blocks a legitimate operation).
/// </summary>
public interface ISecurityAudit
{
    SecurityEvent Record(SecurityEventType type, SecuritySeverity severity, string actor, string target, string detail);
}

/// <summary>No-op audit. Records nothing. Used when no security log is configured.</summary>
public sealed class NullSecurityAudit : ISecurityAudit
{
    public static readonly NullSecurityAudit Instance = new();

    public SecurityEvent Record(SecurityEventType type, SecuritySeverity severity, string actor, string target, string detail) =>
        new(DateTime.UtcNow.ToString("o"), actor, Environment.MachineName, type, severity, target, detail);
}


/// <summary>Read/verify side of the ledger, kept separate from <see cref="ISecurityAudit"/> so a UI
/// can offer an integrity check and a viewer without depending on the concrete log type. Implemented
/// by <see cref="SecurityEventLog"/>.</summary>
public interface ISecurityLogReader
{
    ChainVerificationResult Verify();
    IReadOnlyList<SecurityLogEntry> ReadAll();
}
