using InventoryManager.Core.Security;

namespace InventoryManager.Core.Tests;

public class TempCredentialCleanupTests
{
    private static readonly DateTime Now = new(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void StaleFiles_ReturnsOnlyThoseOlderThanMaxAge()
    {
        var files = new (string, DateTime)[]
        {
            ("fresh.xml", Now.AddMinutes(-5)),   // in-flight handoff - keep
            ("old.xml",   Now.AddHours(-2)),     // orphaned by a prior crash - delete
            ("edge.xml",  Now.AddMinutes(-61)),  // just past the 1h cutoff - delete
        };
        var stale = TempCredentialCleanup.StaleFiles(files, Now, TimeSpan.FromHours(1));
        Assert.Contains("old.xml", stale);
        Assert.Contains("edge.xml", stale);
        Assert.DoesNotContain("fresh.xml", stale);
    }

    [Fact]
    public void StaleFiles_EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(TempCredentialCleanup.StaleFiles([], Now, TimeSpan.FromHours(1)));
    }

    [Fact]
    public void SearchPattern_MatchesThePrefix()
    {
        Assert.StartsWith(TempCredentialCleanup.OffboardCredentialPrefix,
            TempCredentialCleanup.OffboardCredentialSearchPattern);
    }
}
