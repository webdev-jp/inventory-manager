using InventoryManager.Core.Security;

namespace InventoryManager.Core.Tests;

/// <summary>In-memory audit + alert sink used to assert what the decision services record.</summary>
public sealed class RecordingSecurityAudit : ISecurityAudit
{
    public List<SecurityEvent> Events { get; } = [];
    public SecurityEvent Record(SecurityEventType type, SecuritySeverity severity, string actor, string target, string detail)
    {
        var e = new SecurityEvent(DateTime.UtcNow.ToString("o"), actor, "TESTHOST", type, severity, target, detail);
        Events.Add(e);
        return e;
    }
}

public sealed class RecordingAlertSink : ISecurityAlertSink
{
    public List<SecurityEvent> Raised { get; } = [];
    public void Raise(SecurityEvent securityEvent) => Raised.Add(securityEvent);
}

public class SecurityEventChainTests
{
    private static SecurityEvent E(string detail) =>
        new("2026-09-15T00:00:00.0000000Z", "suadmin", "HOST", SecurityEventType.LoginSuccess, SecuritySeverity.Info, "t", detail);

    [Fact]
    public void ComputeHash_IsDeterministic_AndSensitiveToPrevHash()
    {
        var e = E("x");
        Assert.Equal(SecurityEventChain.ComputeHash(SecurityEventChain.GenesisHash, e),
                     SecurityEventChain.ComputeHash(SecurityEventChain.GenesisHash, e));
        Assert.NotEqual(SecurityEventChain.ComputeHash(SecurityEventChain.GenesisHash, e),
                        SecurityEventChain.ComputeHash("deadbeef", e));
    }

    private static List<SecurityLogEntry> Chain(params SecurityEvent[] events)
    {
        var list = new List<SecurityLogEntry>();
        var prev = SecurityEventChain.GenesisHash;
        foreach (var e in events)
        {
            var hash = SecurityEventChain.ComputeHash(prev, e);
            list.Add(new SecurityLogEntry(e, prev, hash));
            prev = hash;
        }
        return list;
    }

    [Fact]
    public void Verify_IntactChain_IsAccepted()
    {
        Assert.True(SecurityEventChain.Verify(Chain(E("a"), E("b"), E("c"))).IsIntact);
    }

    [Fact]
    public void Verify_AlteredMiddleEntry_IsDetectedAtThatIndex()
    {
        var chain = Chain(E("a"), E("b"), E("c"));
        // Attacker rewrites entry 1's detail but leaves its stored hash (and the links) as-is.
        chain[1] = chain[1] with { Event = chain[1].Event with { Detail = "tampered" } };
        var result = SecurityEventChain.Verify(chain);
        Assert.False(result.IsIntact);
        Assert.Equal(1, result.FirstBrokenIndex);
    }

    [Fact]
    public void Verify_DeletedEntry_BreaksTheLink()
    {
        var chain = Chain(E("a"), E("b"), E("c"));
        chain.RemoveAt(1); // drop the middle - entry formerly at index 2 now mis-links
        Assert.False(SecurityEventChain.Verify(chain).IsIntact);
    }
}

public class SecurityEventLogTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"sec_{Guid.NewGuid():N}.jsonl");

    public void Dispose()
    {
        foreach (var p in new[] { _path, _path + ".lock" })
        {
            if (File.Exists(p)) File.Delete(p);
        }
    }

    [Fact]
    public void Append_ThenRead_ChainsFromGenesis_AndVerifies()
    {
        var log = new SecurityEventLog(_path);
        log.Record(SecurityEventType.LoginSuccess, SecuritySeverity.Info, "suadmin", "", "login");
        log.Record(SecurityEventType.PrivilegedGrantRefused, SecuritySeverity.Warning, "ordinarysu", "Domain Admins", "refused");

        var entries = log.ReadAll();
        Assert.Equal(2, entries.Count);
        Assert.Equal(SecurityEventChain.GenesisHash, entries[0].PrevHash);
        Assert.Equal(entries[0].Hash, entries[1].PrevHash);
        Assert.True(log.Verify().IsIntact);
    }

    [Fact]
    public void Verify_DetectsHandEditOfTheFile()
    {
        var log = new SecurityEventLog(_path);
        log.Record(SecurityEventType.LoginSuccess, SecuritySeverity.Info, "a", "", "one");
        log.Record(SecurityEventType.LoginSuccess, SecuritySeverity.Info, "b", "", "two");

        // Someone edits the ledger on disk to hide what actor "b" did.
        var text = File.ReadAllText(_path).Replace("two", "scrubbed");
        File.WriteAllText(_path, text);

        Assert.False(log.Verify().IsIntact);
    }

    [Fact]
    public void Record_AlertsOnlyAtOrAboveThreshold()
    {
        var sink = new RecordingAlertSink();
        var log = new SecurityEventLog(_path, sink, SecuritySeverity.Warning);
        log.Record(SecurityEventType.LoginSuccess, SecuritySeverity.Info, "a", "", "info-noise");
        log.Record(SecurityEventType.HoneytokenTripped, SecuritySeverity.Critical, "a", "bait", "trip!");

        Assert.Single(sink.Raised);
        Assert.Equal(SecurityEventType.HoneytokenTripped, sink.Raised[0].Type);
    }
}

public class HoneytokenPolicyTests
{
    [Fact]
    public void IsHoneytokenGroup_MatchesCaseInsensitively()
    {
        Assert.True(HoneytokenPolicy.IsHoneytokenGroup("da-shadow", ["DA-Shadow"]));
        Assert.False(HoneytokenPolicy.IsHoneytokenGroup("Real Group", ["DA-Shadow"]));
    }

    [Fact]
    public void EmptyList_MatchesNothing_SoFeatureIsInertUntilConfigured()
    {
        Assert.False(HoneytokenPolicy.IsHoneytokenGroup("anything", []));
        Assert.False(HoneytokenPolicy.IsHoneytokenUser("anyone", []));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NullOrBlank_NeverMatches(string? value)
    {
        Assert.False(HoneytokenPolicy.IsHoneytokenGroup(value, ["x"]));
    }

    [Fact]
    public void RealDefaultLists_AreArmed_WithTheReservedDecoys()
    {
        // Pins the decoys so an accidental future edit that empties the tripwire is caught. These
        // names must match the bait objects created in AD per the hardening runbook.
        Assert.NotEmpty(HoneytokenPolicy.Groups);
        Assert.NotEmpty(HoneytokenPolicy.Usernames);
        Assert.True(HoneytokenPolicy.IsHoneytokenGroup("Domain Admins - Legacy"));
        Assert.True(HoneytokenPolicy.IsHoneytokenGroup("Tier0-Admins"));
        Assert.True(HoneytokenPolicy.IsHoneytokenUser("da-breakglass"));
        Assert.True(HoneytokenPolicy.IsHoneytokenUser("svc-admbackup"));
    }
}
