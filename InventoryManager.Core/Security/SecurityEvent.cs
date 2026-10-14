namespace InventoryManager.Core.Security;

/// <summary>What kind of security-relevant thing happened. Kept as a stable enum (its ToString()
/// name is part of the tamper-evidence hash, see SecurityEventChain) so renaming a member would
/// invalidate old chains - add new members, don't rename existing ones.</summary>
public enum SecurityEventType
{
    LoginSuccess,
    LoginFailure,
    GateFailure,
    PrivilegedGrantRefused,
    NestedPrivilegeRefused,
    PrivilegedGrantAllowed,
    OuPolicyBlocked,
    FormulaInjectionNeutralized,
    MalformedRowQuarantined,
    HoneytokenTripped,
    InventoryFieldChanged,
    SessionEnded,
    PermissionsCopied,
    PermissionCopyRefused,
    OffboardingUndone,
    ProtectedAccountRefused,
    ScriptSignatureRefused,
    TerminationScheduled,
    TerminationScheduleChanged,
    TerminationScheduleCancelled,
    TerminationScheduleCompleted,
}

public enum SecuritySeverity
{
    Info,
    Warning,
    Critical,
}

/// <summary>
/// One security-audit record. Immutable; the exact field set and order feed the hash chain, so
/// treat this shape as an on-disk contract.
/// </summary>
public sealed record SecurityEvent(
    string TimestampUtc,
    string Actor,
    string Machine,
    SecurityEventType Type,
    SecuritySeverity Severity,
    string Target,
    string Detail);

/// <summary>A persisted event plus its place in the tamper-evident chain: the hash of the
/// previous entry and this entry's own hash.</summary>
public sealed record SecurityLogEntry(SecurityEvent Event, string PrevHash, string Hash);
