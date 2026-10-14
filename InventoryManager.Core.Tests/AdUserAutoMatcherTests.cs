using InventoryManager.Core.Ad;

namespace InventoryManager.Core.Tests;

public class AdUserAutoMatcherTests
{
    private static AdUserSummary U(string sam, string given, string sur) => new(sam, given, sur, "", $"CN={sam}");

    [Fact]
    public void NoOneWithThatLastName_IsNoCandidates()
    {
        var r = AdUserAutoMatcher.Match([], "John");
        Assert.Equal(AutoMatchOutcome.NoCandidates, r.Outcome);
        Assert.Null(r.User);
    }

    [Fact]
    public void OneSurname_FirstNameMatches_IsMatched_CaseInsensitive()
    {
        var r = AdUserAutoMatcher.Match([U("jsmith", "John", "Smith")], "  jOHN ");
        Assert.Equal(AutoMatchOutcome.Matched, r.Outcome);
        Assert.Equal("jsmith", r.User!.SamAccountName);
    }

    [Fact]
    public void OneSurname_NoFirstNameTyped_IsMatched()
    {
        var r = AdUserAutoMatcher.Match([U("jsmith", "John", "Smith")], "");
        Assert.Equal(AutoMatchOutcome.Matched, r.Outcome);
        Assert.Equal("jsmith", r.User!.SamAccountName);
    }

    [Fact]
    public void OneSurname_FirstNameDiffers_IsOnlyASuggestion_NeverMatched()
    {
        // Typing "Jon Smith" must not silently link to the only Smith in AD, "Mary".
        var r = AdUserAutoMatcher.Match([U("msmith", "Mary", "Smith")], "Jon");
        Assert.Equal(AutoMatchOutcome.PossibleOnly, r.Outcome);
        Assert.Equal("msmith", r.User!.SamAccountName);
    }

    [Fact]
    public void SeveralSurnames_FirstNameNarrowsToOne_IsMatched()
    {
        var r = AdUserAutoMatcher.Match([U("jsmith", "John", "Smith"), U("msmith", "Mary", "Smith")], "Mary");
        Assert.Equal(AutoMatchOutcome.Matched, r.Outcome);
        Assert.Equal("msmith", r.User!.SamAccountName);
        Assert.Equal(2, r.CandidateCount);
    }

    [Fact]
    public void SeveralSurnames_NoFirstName_IsSeveral()
    {
        var r = AdUserAutoMatcher.Match([U("jsmith", "John", "Smith"), U("msmith", "Mary", "Smith")], "");
        Assert.Equal(AutoMatchOutcome.Several, r.Outcome);
        Assert.Null(r.User);
        Assert.Equal(2, r.CandidateCount);
    }

    [Fact]
    public void SeveralSurnames_FirstNameFitsNone_IsSeveral()
    {
        var r = AdUserAutoMatcher.Match([U("jsmith", "John", "Smith"), U("msmith", "Mary", "Smith")], "Pat");
        Assert.Equal(AutoMatchOutcome.Several, r.Outcome);
        Assert.Null(r.User);
    }

    [Fact]
    public void TwoPeopleWithTheSameFullName_IsSeveral_NeverGuessed()
    {
        var r = AdUserAutoMatcher.Match([U("jsmith", "John", "Smith"), U("jsmith2", "John", "Smith")], "John");
        Assert.Equal(AutoMatchOutcome.Several, r.Outcome);
        Assert.Null(r.User);
        Assert.Equal(2, r.CandidateCount);
    }
}
