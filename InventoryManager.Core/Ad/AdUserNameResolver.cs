namespace InventoryManager.Core.Ad;

public enum NameMatchOutcome
{
    SingleMatch,
    Ambiguous,
    NoMatch,
}

public sealed record NameMatchResult(NameMatchOutcome Outcome, AdUserSummary? Match, IReadOnlyList<AdUserSummary> Candidates);

/// <summary>
/// Shared name-based resolution logic behind both Check AD and Match User (Invoke-AdCheckForSelected
/// / Invoke-MatchUserToAd's identical candidate-narrowing block): given every AD user with an exact
/// Surname match, narrow to one by First Name if that alone doesn't disambiguate, otherwise leave it
/// ambiguous for a human to pick (the caller shows Show-AdUserPickerDialog's C# equivalent).
/// </summary>
public static class AdUserNameResolver
{
    public static NameMatchResult Resolve(IReadOnlyList<AdUserSummary> exactSurnameCandidates, string firstName)
    {
        if (exactSurnameCandidates.Count == 0)
        {
            return new NameMatchResult(NameMatchOutcome.NoMatch, null, exactSurnameCandidates);
        }
        if (exactSurnameCandidates.Count == 1)
        {
            return new NameMatchResult(NameMatchOutcome.SingleMatch, exactSurnameCandidates[0], exactSurnameCandidates);
        }
        if (!string.IsNullOrWhiteSpace(firstName))
        {
            var narrowed = exactSurnameCandidates
                .Where(c => string.Equals(c.GivenName.Trim(), firstName.Trim(), StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (narrowed.Count == 1)
            {
                return new NameMatchResult(NameMatchOutcome.SingleMatch, narrowed[0], exactSurnameCandidates);
            }
        }
        return new NameMatchResult(NameMatchOutcome.Ambiguous, null, exactSurnameCandidates);
    }
}
