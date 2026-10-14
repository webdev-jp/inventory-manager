namespace InventoryManager.Core.Ad;

public enum AutoMatchOutcome
{
    /// <summary>One AD user clearly is this person - safe to fill the Username in automatically.</summary>
    Matched,
    /// <summary>Exactly one AD user has that last name, but the typed first name doesn't match theirs
    /// (e.g. "Jon Smith" vs the only Smith, "Mary"). Offered as a suggestion, never filled in.</summary>
    PossibleOnly,
    /// <summary>More than one AD user fits - a person has to choose.</summary>
    Several,
    /// <summary>Nobody in AD has that last name.</summary>
    NoCandidates,
}

public sealed record AutoMatchResult(AutoMatchOutcome Outcome, AdUserSummary? User, int CandidateCount);

/// <summary>
/// Decides whether the name typed into an inventory record is enough to link it to an AD account
/// without asking - used by the Add/Edit dialog so the Username fills itself in as soon as the name
/// is entered. Stricter than <see cref="AdUserNameResolver"/> (which Match User/Check AD use and
/// which accepts the only candidate with a given surname whatever first name was typed): because
/// this runs automatically while someone is typing, a first name that doesn't fit is never silently
/// accepted - it's downgraded to a suggestion.
/// </summary>
public static class AdUserAutoMatcher
{
    public static AutoMatchResult Match(IReadOnlyList<AdUserSummary> exactSurnameCandidates, string firstName)
    {
        var count = exactSurnameCandidates.Count;
        if (count == 0)
        {
            return new AutoMatchResult(AutoMatchOutcome.NoCandidates, null, 0);
        }

        var first = firstName.Trim();
        if (first.Length == 0)
        {
            return count == 1
                ? new AutoMatchResult(AutoMatchOutcome.Matched, exactSurnameCandidates[0], 1)
                : new AutoMatchResult(AutoMatchOutcome.Several, null, count);
        }

        var sameFirst = exactSurnameCandidates
            .Where(c => string.Equals(c.GivenName.Trim(), first, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (sameFirst.Count == 1)
        {
            return new AutoMatchResult(AutoMatchOutcome.Matched, sameFirst[0], count);
        }
        if (sameFirst.Count > 1)
        {
            return new AutoMatchResult(AutoMatchOutcome.Several, null, sameFirst.Count);
        }
        return count == 1
            ? new AutoMatchResult(AutoMatchOutcome.PossibleOnly, exactSurnameCandidates[0], 1)
            : new AutoMatchResult(AutoMatchOutcome.Several, null, count);
    }
}
