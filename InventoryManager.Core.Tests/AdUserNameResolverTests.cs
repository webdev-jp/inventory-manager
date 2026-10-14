using InventoryManager.Core.Ad;

namespace InventoryManager.Core.Tests;

public class AdUserNameResolverTests
{
    private static AdUserSummary MakeUser(string sam, string given, string surname) =>
        new(sam, given, surname, "", $"CN={given} {surname},DC=x");

    [Fact]
    public void NoCandidates_ReturnsNoMatch()
    {
        var result = AdUserNameResolver.Resolve([], "Jane");
        Assert.Equal(NameMatchOutcome.NoMatch, result.Outcome);
        Assert.Null(result.Match);
    }

    [Fact]
    public void OneCandidate_ReturnsSingleMatch_RegardlessOfFirstName()
    {
        var candidates = new[] { MakeUser("jsmith", "Jane", "Smith") };
        var result = AdUserNameResolver.Resolve(candidates, "");
        Assert.Equal(NameMatchOutcome.SingleMatch, result.Outcome);
        Assert.Equal("jsmith", result.Match!.SamAccountName);
    }

    [Fact]
    public void MultipleCandidates_NarrowedByFirstName_ReturnsSingleMatch()
    {
        var candidates = new[] { MakeUser("jsmith", "Jane", "Smith"), MakeUser("bsmith", "Bob", "Smith") };
        var result = AdUserNameResolver.Resolve(candidates, "Bob");
        Assert.Equal(NameMatchOutcome.SingleMatch, result.Outcome);
        Assert.Equal("bsmith", result.Match!.SamAccountName);
    }

    [Fact]
    public void MultipleCandidates_FirstNameCaseInsensitive()
    {
        var candidates = new[] { MakeUser("jsmith", "Jane", "Smith"), MakeUser("bsmith", "Bob", "Smith") };
        var result = AdUserNameResolver.Resolve(candidates, "bob");
        Assert.Equal(NameMatchOutcome.SingleMatch, result.Outcome);
    }

    [Fact]
    public void MultipleCandidates_NoFirstNameGiven_IsAmbiguous()
    {
        var candidates = new[] { MakeUser("jsmith", "Jane", "Smith"), MakeUser("bsmith", "Bob", "Smith") };
        var result = AdUserNameResolver.Resolve(candidates, "");
        Assert.Equal(NameMatchOutcome.Ambiguous, result.Outcome);
        Assert.Equal(2, result.Candidates.Count);
    }

    [Fact]
    public void MultipleCandidates_FirstNameMatchesNoOne_IsAmbiguous()
    {
        var candidates = new[] { MakeUser("jsmith", "Jane", "Smith"), MakeUser("bsmith", "Bob", "Smith") };
        var result = AdUserNameResolver.Resolve(candidates, "Zoe");
        Assert.Equal(NameMatchOutcome.Ambiguous, result.Outcome);
    }

    [Fact]
    public void MultipleCandidates_FirstNameMatchesMultiple_StillAmbiguous()
    {
        var candidates = new[] { MakeUser("j1", "Jane", "Smith"), MakeUser("j2", "Jane", "Smith") };
        var result = AdUserNameResolver.Resolve(candidates, "Jane");
        Assert.Equal(NameMatchOutcome.Ambiguous, result.Outcome);
    }
}
