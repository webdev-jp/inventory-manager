using InventoryManager.Core.Ad;

namespace InventoryManager.Core.Tests;

public class LoginGateServiceTests
{
    private const string Domain = "contoso.local";
    private const string SuGroup = "InventoryManager";

    [Fact]
    public void CheckGateAccount_ValidPassword_ReturnsTrue()
    {
        var ad = new FakeAdService { ValidateCredentialsOverride = (_, u, p) => u == "inv-gateaccount" && p == "correct" };
        Assert.True(LoginGateService.CheckGateAccount(ad, Domain, "inv-gateaccount", "correct"));
    }

    [Fact]
    public void CheckGateAccount_WrongPassword_ReturnsFalse()
    {
        var ad = new FakeAdService { ValidateCredentialsOverride = (_, u, p) => u == "inv-gateaccount" && p == "correct" };
        Assert.False(LoginGateService.CheckGateAccount(ad, Domain, "inv-gateaccount", "wrong"));
    }

    [Fact]
    public void AttemptSuLogin_EmptyUsername_ReturnsEmptyUsername()
    {
        var ad = new FakeAdService();
        var result = LoginGateService.AttemptSuLogin(ad, Domain, SuGroup, "  ", "pw");
        Assert.Equal(SuLoginAttemptOutcome.EmptyUsername, result.Outcome);
    }

    [Fact]
    public void AttemptSuLogin_WrongCredentials_ReturnsInvalidCredentials()
    {
        var ad = new FakeAdService { ValidateCredentialsOverride = (_, _, _) => false };
        var result = LoginGateService.AttemptSuLogin(ad, Domain, SuGroup, "jsmith", "wrong");
        Assert.Equal(SuLoginAttemptOutcome.InvalidCredentials, result.Outcome);
    }

    [Fact]
    public void AttemptSuLogin_ValidCredentialsNotInGroup_ReturnsNotInSuGroup()
    {
        var ad = new FakeAdService
        {
            ValidateCredentialsOverride = (_, _, _) => true,
            IsUserInGroupOverride = (_, _, _) => false,
        };
        var result = LoginGateService.AttemptSuLogin(ad, Domain, SuGroup, "jsmith", "correct");
        Assert.Equal(SuLoginAttemptOutcome.NotInSuGroup, result.Outcome);
    }

    [Fact]
    public void AttemptSuLogin_ValidAndInGroup_ReturnsSuccessWithQualifiedUsername()
    {
        var ad = new FakeAdService
        {
            ValidateCredentialsOverride = (_, _, _) => true,
            IsUserInGroupOverride = (_, _, _) => true,
            DomainNetBiosName = "CONTOSO",
        };
        var result = LoginGateService.AttemptSuLogin(ad, Domain, SuGroup, "jsmith", "correct");
        Assert.Equal(SuLoginAttemptOutcome.Success, result.Outcome);
        Assert.Equal(@"CONTOSO\jsmith", result.QualifiedUsername);
    }

    [Theory]
    [InlineData("jsmith", "CONTOSO", @"CONTOSO\jsmith")]
    [InlineData(@"CONTOSO\jsmith", "CONTOSO", @"CONTOSO\jsmith")]
    [InlineData("jsmith@contoso.com", "CONTOSO", "jsmith@contoso.com")]
    [InlineData("jsmith", null, "jsmith")]
    public void QualifyUsername_HandlesAlreadyQualifiedAndUnresolvedNetBios(string username, string? netBios, string expected)
    {
        Assert.Equal(expected, LoginGateService.QualifyUsername(username, netBios));
    }
}
