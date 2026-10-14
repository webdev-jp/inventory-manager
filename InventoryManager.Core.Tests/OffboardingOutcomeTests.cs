using InventoryManager.Core.Offboarding;

namespace InventoryManager.Core.Tests;

public class OffboardingOutcomeTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", name));

    private static OffboardingOutcome Eval(string json, string user = "jdoe") =>
        OffboardingOutcomeEvaluator.Evaluate(OffboardingOutcomeEvaluator.TryParseSession(json), user);

    private static string Session(params string[] runs) =>
        $$"""{ "SchemaVersion": 1, "Mode": "Offboard", "ExpectedUser": "jdoe", "Runs": [ {{string.Join(",", runs)}} ] }""";

    private static string Run(string sam = "jdoe", bool test = false, string? ticket = "T-1", bool completed = true,
        string steps = """{ "Id": "DisableAD", "Result": "OK", "Error": null }""",
        string journal = """{ "Step": "DisableAD", "Data": { "WasEnabled": true } }""") =>
        $$"""
        { "SamAccountName": "{{sam}}", "TestMode": {{(test ? "true" : "false")}}, "Ticket": {{(ticket is null ? "null" : $"\"{ticket}\"")}},
          "CompletedAt": {{(completed ? "\"2026-09-30T10:00:00Z\"" : "null")}},
          "Steps": [ {{steps}} ], "Journal": [ {{(test ? "" : journal)}} ] }
        """;

    // ---- real script output ----

    [Fact]
    public void RealScriptOutput_TestRunThenRealRun_IsSucceededWithTicketAndUndoableChanges()
    {
        var outcome = Eval(Fixture("offboard-session-sample.json"));
        Assert.Equal(OffboardingOutcomeKind.Succeeded, outcome.Kind);
        Assert.Equal("T-4242", outcome.Ticket);
        Assert.Empty(outcome.FailedSteps);
        Assert.True(outcome.CanUndo);
        Assert.Equal(14, outcome.UndoableChangeCount);          // 17 journal entries minus 3 not-undoable
        Assert.Equal(3, outcome.NotUndoableNotes.Count);        // password, revoked sessions, OneDrive archive
        Assert.Contains(outcome.NotUndoableNotes, n => n.Contains("Password was randomized"));
    }

    [Fact]
    public void RealScriptOutput_UndoReport_Parses()
    {
        var report = OffboardingOutcomeEvaluator.TryParseUndoReport(Fixture("offboard-undo-sample.json"));
        Assert.NotNull(report);
        Assert.Equal("jdoe", report.SamAccountName);
        Assert.NotNull(report.CompletedAt);
        Assert.Contains(report.Undone, u => u.StartsWith("DisableAD:"));
        Assert.Contains(report.Undone, u => u.Contains("boss@contoso.org's"));  // ' escape decoded
        Assert.Single(report.Failed);
        Assert.Equal(3, report.NotUndoable.Count);
    }

    // ---- nothing to act on ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ not json")]
    public void MissingOrUnreadableFile_IsNoResult(string? json)
    {
        var outcome = OffboardingOutcomeEvaluator.Evaluate(OffboardingOutcomeEvaluator.TryParseSession(json), "jdoe");
        Assert.Equal(OffboardingOutcomeKind.NoResult, outcome.Kind);
        Assert.False(outcome.CanUndo);
    }

    [Fact]
    public void ToolOpenedAndClosedWithoutRunning_IsNothingRan()
    {
        Assert.Equal(OffboardingOutcomeKind.NothingRan, Eval(Session()).Kind);
    }

    [Fact]
    public void TestRunsOnly_IsNothingRan()
    {
        var outcome = Eval(Session(Run(test: true), Run(test: true)));
        Assert.Equal(OffboardingOutcomeKind.NothingRan, outcome.Kind);
        Assert.Null(outcome.Ticket);
    }

    [Fact]
    public void RealRunOnADifferentUserOnly_IsOtherUserOnly()
    {
        var outcome = Eval(Session(Run(sam: "asmith")));
        Assert.Equal(OffboardingOutcomeKind.OtherUserOnly, outcome.Kind);
        Assert.Equal(["asmith"], outcome.OtherUsers);
        Assert.False(outcome.CanUndo);
    }

    // ---- real runs for the device's user ----

    [Fact]
    public void UsernameMatch_IsCaseInsensitive()
    {
        Assert.Equal(OffboardingOutcomeKind.Succeeded, Eval(Session(Run(sam: "JDoe"))).Kind);
    }

    [Fact]
    public void RealRunWithFailedStep_IsCompletedWithErrors_ListingTheStep()
    {
        var outcome = Eval(Session(Run(steps: """
            { "Id": "DisableAD", "Result": "OK", "Error": null },
            { "Id": "ConvertShared", "Result": "FAILED", "Error": "Mailbox not found" }
            """)));
        Assert.Equal(OffboardingOutcomeKind.CompletedWithErrors, outcome.Kind);
        Assert.Equal(["ConvertShared: Mailbox not found"], outcome.FailedSteps);
        Assert.True(outcome.CanUndo);   // the AD disable that DID happen can still be undone
    }

    [Fact]
    public void StepThatFailedThenSucceededOnARerun_IsNotAFailure()
    {
        var outcome = Eval(Session(
            Run(steps: """{ "Id": "ConvertShared", "Result": "FAILED", "Error": "throttled" }"""),
            Run(steps: """{ "Id": "ConvertShared", "Result": "OK", "Error": null }""")));
        Assert.Equal(OffboardingOutcomeKind.Succeeded, outcome.Kind);
    }

    [Fact]
    public void RunThatNeverFinished_CountsAsAnError()
    {
        var outcome = Eval(Session(Run(completed: false)));
        Assert.Equal(OffboardingOutcomeKind.CompletedWithErrors, outcome.Kind);
        Assert.Contains(outcome.FailedSteps, f => f.Contains("did not finish"));
    }

    [Fact]
    public void RealRunsOnBothUsers_DecidesOnTheDevicesUser_AndReportsTheOther()
    {
        var outcome = Eval(Session(Run(sam: "asmith"), Run(sam: "jdoe")));
        Assert.Equal(OffboardingOutcomeKind.Succeeded, outcome.Kind);
        Assert.Equal(["asmith"], outcome.OtherUsers);
    }

    [Fact]
    public void TestRunsForTheSameUser_AreIgnoredForStepResults()
    {
        var outcome = Eval(Session(
            Run(test: true, steps: """{ "Id": "RemoveLicenses", "Result": "FAILED", "Error": "blocked" }"""),
            Run()));
        Assert.Equal(OffboardingOutcomeKind.Succeeded, outcome.Kind);
    }

    [Fact]
    public void Ticket_IsTheLatestNonEmptyOne()
    {
        Assert.Equal("T-2", Eval(Session(Run(ticket: "T-1"), Run(ticket: "T-2"), Run(ticket: " "))).Ticket);
    }

    [Fact]
    public void RealRunThatChangedNothingUndoable_CannotUndo()
    {
        var outcome = Eval(Session(Run(journal: """{ "Step": "RandomPassword", "Data": { "NotUndoable": "Reset it." } }""")));
        Assert.False(outcome.CanUndo);
        Assert.Equal(["Reset it."], outcome.NotUndoableNotes);
    }

    [Fact]
    public void FilePrefixes_MatchTheirSearchPatterns()
    {
        Assert.StartsWith(OffboardingOutcomeEvaluator.SessionFilePrefix, OffboardingOutcomeEvaluator.SessionFileSearchPattern);
        Assert.StartsWith(OffboardingOutcomeEvaluator.UndoReportFilePrefix, OffboardingOutcomeEvaluator.UndoReportFileSearchPattern);
    }
}
