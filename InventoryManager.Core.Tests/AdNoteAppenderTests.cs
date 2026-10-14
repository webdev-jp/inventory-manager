using InventoryManager.Core.Ad;

namespace InventoryManager.Core.Tests;

public class AdNoteAppenderTests
{
    [Fact]
    public void Combine_NoExistingContent_ReturnsJustTheNewEntry()
    {
        var result = AdNoteAppender.Combine("", "[2026-01-01 09:00] Onboarded by X - Ticket # T1");
        Assert.Equal("[2026-01-01 09:00] Onboarded by X - Ticket # T1", result);
    }

    [Fact]
    public void Combine_WithExistingContent_AppendsWithDivider()
    {
        var result = AdNoteAppender.Combine(
            "[2026-01-01 09:00] Onboarded by X - Ticket # T1",
            "[2026-01-02 10:00] Transferred by Y - Ticket # T2");

        Assert.Contains("T1", result);
        Assert.Contains("T2", result);
        Assert.Contains("----------", result);
        // Oldest entry stays first, newest appended at the bottom - a chronological log.
        Assert.True(result.IndexOf("T1", StringComparison.Ordinal) < result.IndexOf("T2", StringComparison.Ordinal));
    }

    [Fact]
    public void Combine_ManyEntries_DropsOldestFirst_KeepsNewest()
    {
        var existing = "";
        for (var i = 1; i <= 40; i++)
        {
            existing = AdNoteAppender.Combine(existing,
                $"[2026-01-01 09:00] Transferred by Test Admin (testadmin) - Ticket # T{i}. Changed: Manager: Old Manager (om) -> New Manager {i} (nm{i}); OU: OU=Old,DC=x -> OU=New{i},DC=x");
        }

        Assert.True(existing.Length <= 1000, $"Expected <= 1000 chars, got {existing.Length}");
        Assert.Contains("T40", existing); // newest must survive
        Assert.DoesNotContain("T1.", existing); // oldest must have been dropped
    }

    [Fact]
    public void Combine_SingleEntryAloneExceedsCap_HardTruncatesRatherThanFail()
    {
        var hugeEntry = "[2026-01-01 09:00] " + new string('x', 2000);
        var result = AdNoteAppender.Combine("", hugeEntry);
        Assert.Equal(1000, result.Length);
        // Kept the TAIL end of the oversized entry, not the front.
        Assert.EndsWith("xxxx", result);
    }
}
