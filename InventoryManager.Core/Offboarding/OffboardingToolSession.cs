using System.Text.Json;

namespace InventoryManager.Core.Offboarding;

/// <summary>
/// The session record Offboarding2026.ps1 writes to its <c>-ResultFile</c>: created the moment
/// the tool opens, rewritten after every step of every Run, and stamped with <see cref="ClosedAt"/>
/// when the window closes. Shape is owned by the script (see its Save-SessionFile / Run handler);
/// every property is optional here so an older or partial file still parses.
/// </summary>
public sealed class OffboardingSessionRecord
{
    public int SchemaVersion { get; set; }
    public string? Mode { get; set; }
    public string? ExpectedUser { get; set; }
    public string? StartedAt { get; set; }
    public string? ClosedAt { get; set; }
    public List<OffboardingRun> Runs { get; set; } = [];
}

/// <summary>One click of the tool's Run button.</summary>
public sealed class OffboardingRun
{
    public string SamAccountName { get; set; } = "";
    public bool TestMode { get; set; }
    public string? Ticket { get; set; }
    public string? Operator { get; set; }
    public string? StartedAt { get; set; }
    /// <summary>Null when the run never finished (tool crashed or was killed mid-run).</summary>
    public string? CompletedAt { get; set; }
    public int ErrorCount { get; set; }
    public List<OffboardingStepResult> Steps { get; set; } = [];
    /// <summary>Undo journal - the "before" state of each change a real run made.</summary>
    public List<OffboardingJournalEntry> Journal { get; set; } = [];
}

public sealed class OffboardingStepResult
{
    public string Id { get; set; } = "";
    /// <summary>"OK", "Simulated" (test mode) or "FAILED".</summary>
    public string Result { get; set; } = "";
    public string? Error { get; set; }
}

public sealed class OffboardingJournalEntry
{
    public string Step { get; set; } = "";
    public JsonElement Data { get; set; }

    /// <summary>The script's note for a change undo can't reverse (password, revoked sessions,
    /// OneDrive archive), or null for an ordinary undoable change.</summary>
    public string? NotUndoableNote =>
        Data.ValueKind == JsonValueKind.Object
        && Data.TryGetProperty("NotUndoable", out var note)
        && note.ValueKind == JsonValueKind.String
            ? note.GetString()
            : null;
}

/// <summary>What the script's undo mode (<c>-UndoFrom</c>) reports back via <c>-ResultFile</c>.</summary>
public sealed class OffboardingUndoReport
{
    public string? SamAccountName { get; set; }
    public string? CompletedAt { get; set; }
    public List<string> Undone { get; set; } = [];
    public List<string> Failed { get; set; } = [];
    public List<string> NotUndoable { get; set; } = [];
}

public enum OffboardingOutcomeKind
{
    /// <summary>No readable session record - the script never started (e.g. blocked by the
    /// AllSigned signature check) or crashed before writing anything.</summary>
    NoResult,
    /// <summary>The tool opened but no real (non-test) run happened - closed straight away,
    /// cancelled at the confirmation, or test runs only.</summary>
    NothingRan,
    /// <summary>Real runs happened, but only against users other than the device's.</summary>
    OtherUserOnly,
    /// <summary>Real run(s) for the device's user and every step's latest result is OK.</summary>
    Succeeded,
    /// <summary>Real run(s) for the device's user, with at least one step still failed, or a run
    /// that never finished.</summary>
    CompletedWithErrors,
}

public sealed record OffboardingOutcome(
    OffboardingOutcomeKind Kind,
    string? Ticket,
    IReadOnlyList<string> FailedSteps,
    IReadOnlyList<string> OtherUsers,
    int UndoableChangeCount,
    IReadOnlyList<string> NotUndoableNotes)
{
    public bool CanUndo => UndoableChangeCount > 0;
}

/// <summary>
/// Decides what Inventory Manager may do once the offboarding tool closes, from the session record
/// the tool wrote. Pure logic (no I/O) so it's unit-testable; the caller reads the file.
/// The point is that the device/computer side is only ever offered after a real run actually
/// happened for the device's own user - never just because the window closed.
/// </summary>
public static class OffboardingOutcomeEvaluator
{
    /// <summary>Filename prefixes for the per-launch files in %TEMP% (user-private). Shared by the
    /// writer and the startup sweep so they can't drift apart.</summary>
    public const string SessionFilePrefix = "invmgr-offboard-session-";
    public const string UndoReportFilePrefix = "invmgr-offboard-undo-";
    public const string SessionFileSearchPattern = "invmgr-offboard-session-*.json";
    public const string UndoReportFileSearchPattern = "invmgr-offboard-undo-*.json";

    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Returns null for a missing, empty or malformed file - all of which mean "the
    /// script didn't tell us anything", so the caller must not act on it.</summary>
    public static OffboardingSessionRecord? TryParseSession(string? json) => TryParse<OffboardingSessionRecord>(json);

    public static OffboardingUndoReport? TryParseUndoReport(string? json) => TryParse<OffboardingUndoReport>(json);

    private static T? TryParse<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<T>(json.TrimStart('﻿'), ReadOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static OffboardingOutcome Evaluate(OffboardingSessionRecord? record, string expectedUsername)
    {
        if (record is null)
        {
            return new OffboardingOutcome(OffboardingOutcomeKind.NoResult, null, [], [], 0, []);
        }

        var realRuns = record.Runs.Where(r => !r.TestMode).ToList();
        var mine = realRuns.Where(r => string.Equals(r.SamAccountName, expectedUsername, StringComparison.OrdinalIgnoreCase)).ToList();
        var others = realRuns
            .Select(r => r.SamAccountName)
            .Where(s => !string.Equals(s, expectedUsername, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (mine.Count == 0)
        {
            var kind = others.Count > 0 ? OffboardingOutcomeKind.OtherUserOnly : OffboardingOutcomeKind.NothingRan;
            return new OffboardingOutcome(kind, null, [], others, 0, []);
        }

        // A step that failed in one run but succeeded in a later re-run isn't a failure any more -
        // only each step's LATEST real result counts.
        var latest = new Dictionary<string, OffboardingStepResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var step in mine.SelectMany(r => r.Steps))
        {
            latest[step.Id] = step;
        }
        var failed = latest.Values
            .Where(s => string.Equals(s.Result, "FAILED", StringComparison.OrdinalIgnoreCase))
            .Select(s => string.IsNullOrWhiteSpace(s.Error) ? s.Id : $"{s.Id}: {s.Error}")
            .ToList();
        if (mine.Any(r => string.IsNullOrEmpty(r.CompletedAt)))
        {
            failed.Add("A run did not finish - the tool was closed or crashed partway through.");
        }

        var journal = mine.SelectMany(r => r.Journal).ToList();
        var notUndoable = journal.Select(e => e.NotUndoableNote).OfType<string>().Distinct().ToList();
        var undoable = journal.Count(e => e.NotUndoableNote is null);
        var ticket = mine.Select(r => r.Ticket?.Trim()).LastOrDefault(t => !string.IsNullOrEmpty(t));

        return new OffboardingOutcome(
            failed.Count == 0 ? OffboardingOutcomeKind.Succeeded : OffboardingOutcomeKind.CompletedWithErrors,
            ticket,
            failed,
            others,
            undoable,
            notUndoable);
    }
}
