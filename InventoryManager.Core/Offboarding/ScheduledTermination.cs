namespace InventoryManager.Core.Offboarding;

public enum ScheduledTerminationState
{
    /// <summary>Waiting for its due time, or due and waiting for an admin to act on the prompt.</summary>
    Pending,
    /// <summary>The offboarding tool ran for this user (a real, non-test run).</summary>
    Completed,
    Cancelled,
}

/// <summary>
/// A termination an admin scheduled for later. Nothing runs by itself: once the due time passes,
/// the next admin to open Inventory Manager (or any admin already running it) is prompted to
/// offboard the user through the normal, supervised tool - so it uses that admin's own SU login
/// and Microsoft sign-in, and no credentials are stored anywhere. Keyed by the AD
/// <see cref="Username"/>, with the device row remembered only so the existing Offboard User flow
/// can be started against it.
/// </summary>
public sealed class ScheduledTermination
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public int ItemId { get; set; }
    public DateTime DueAtUtc { get; set; }
    public string Ticket { get; set; } = "";
    public string Notes { get; set; } = "";
    public string ScheduledBy { get; set; } = "";
    public DateTime ScheduledAtUtc { get; set; }
    public ScheduledTerminationState State { get; set; } = ScheduledTerminationState.Pending;

    /// <summary>SU account that clicked Offboard Now and has the tool open, so a second admin who
    /// opens the app meanwhile isn't prompted to offboard the same person. Expires - see
    /// <see cref="ScheduledTerminationRules.ClaimExpiresAfter"/>.</summary>
    public string ClaimedBy { get; set; } = "";
    public DateTime? ClaimedAtUtc { get; set; }

    public string ResolvedBy { get; set; } = "";
    public DateTime? ResolvedAtUtc { get; set; }
    public string ResolutionNote { get; set; } = "";
}

/// <summary>Pure decisions about scheduled terminations (no I/O), so they're unit-testable.</summary>
public static class ScheduledTerminationRules
{
    /// <summary>A claim older than this is treated as abandoned (the admin's PC crashed or they
    /// walked away with the tool open), so another admin may take over.</summary>
    public static readonly TimeSpan ClaimExpiresAfter = TimeSpan.FromHours(2);

    /// <summary>Resolved (completed/cancelled) entries are kept this long as a short history.</summary>
    public static readonly TimeSpan ResolvedHistoryKept = TimeSpan.FromDays(90);

    public static bool IsDue(ScheduledTermination item, DateTime nowUtc) =>
        item.State == ScheduledTerminationState.Pending && item.DueAtUtc <= nowUtc;

    /// <summary>True when ANOTHER admin currently holds a live (non-expired) claim.</summary>
    public static bool IsClaimedByOther(ScheduledTermination item, string me, DateTime nowUtc) =>
        item.State == ScheduledTerminationState.Pending
        && !string.IsNullOrEmpty(item.ClaimedBy)
        && !string.Equals(item.ClaimedBy, me, StringComparison.OrdinalIgnoreCase)
        && item.ClaimedAtUtc is { } claimedAt
        && nowUtc - claimedAt < ClaimExpiresAfter;

    /// <summary>The terminations that should interrupt <paramref name="me"/> right now: due, and
    /// not already being handled by someone else. Earliest due first.</summary>
    public static IReadOnlyList<ScheduledTermination> DueForPrompt(IEnumerable<ScheduledTermination> all, string me, DateTime nowUtc) =>
        all.Where(t => IsDue(t, nowUtc) && !IsClaimedByOther(t, me, nowUtc))
           .OrderBy(t => t.DueAtUtc)
           .ToList();

    /// <summary>Short status text for a list row.</summary>
    public static string Describe(ScheduledTermination item, string me, DateTime nowUtc)
    {
        if (item.State != ScheduledTerminationState.Pending)
        {
            return item.State.ToString();
        }
        if (IsClaimedByOther(item, me, nowUtc))
        {
            return $"Being handled by {item.ClaimedBy}";
        }
        if (IsDue(item, nowUtc))
        {
            var overdue = nowUtc - item.DueAtUtc;
            return overdue < TimeSpan.FromMinutes(1) ? "DUE NOW" : $"DUE - overdue {FormatSpan(overdue)}";
        }
        return $"In {FormatSpan(item.DueAtUtc - nowUtc)}";
    }

    private static string FormatSpan(TimeSpan span)
    {
        if (span.TotalDays >= 1)
        {
            var days = (int)span.TotalDays;
            return days == 1 ? "1 day" : $"{days} days";
        }
        if (span.TotalHours >= 1)
        {
            var hours = (int)span.TotalHours;
            return hours == 1 ? "1 hour" : $"{hours} hours";
        }
        var minutes = Math.Max(1, (int)span.TotalMinutes);
        return minutes == 1 ? "1 minute" : $"{minutes} minutes";
    }
}
