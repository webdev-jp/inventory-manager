using System.Text.Json;
using InventoryManager.Core.Offboarding;

namespace InventoryManager.Core.Persistence;

/// <summary>
/// The shared list of scheduled terminations (<c>scheduled-terminations.json</c>, next to
/// inventory.json). Deliberately its own small file rather than fields on inventory rows: a
/// schedule belongs to a PERSON, not a device (it must survive a device being transferred or the
/// Username being re-linked), and keeping it out of the inventory record means scheduling never
/// triggers the per-field audit/AD-Notes mirror or any AD write at all.
/// <para>Every change is a locked read-modify-write using the same cross-machine lock-file
/// primitive as inventory.json (<see cref="InventoryLock"/>), so two admins editing at once
/// can't lose each other's changes. A file that exists but can't be parsed is never overwritten -
/// the operation throws instead, so a bad read can't silently wipe every schedule.</para>
/// </summary>
public sealed class ScheduledTerminationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly InventoryLock _lock;

    public ScheduledTerminationStore(string filePath)
    {
        _path = filePath;
        _lock = new InventoryLock(filePath + ".lock");
    }

    public string FilePath => _path;

    /// <summary>Everything currently in the file (all states). Missing or empty file is an empty
    /// list; a corrupt file throws <see cref="InvalidDataException"/>.</summary>
    public IReadOnlyList<ScheduledTermination> Load() => ReadAll();

    /// <summary>Pending entries only, earliest due first.</summary>
    public IReadOnlyList<ScheduledTermination> LoadPending() =>
        ReadAll().Where(t => t.State == ScheduledTerminationState.Pending).OrderBy(t => t.DueAtUtc).ToList();

    public ScheduledTermination? FindPendingForUser(string username) =>
        LoadPending().FirstOrDefault(t => string.Equals(t.Username, username, StringComparison.OrdinalIgnoreCase));

    /// <summary>Adds a new pending entry. Returns null (and adds nothing) if the user already has
    /// one pending - edit that one instead.</summary>
    public ScheduledTermination? Add(ScheduledTermination item) => Mutate(list =>
    {
        if (list.Any(t => t.State == ScheduledTerminationState.Pending
                          && string.Equals(t.Username, item.Username, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }
        item.State = ScheduledTerminationState.Pending;
        list.Add(item);
        return item;
    });

    /// <summary>Changes the due time/ticket/notes of a still-pending entry. False if it no longer
    /// exists or is no longer pending (another admin may have completed or cancelled it).</summary>
    public bool Reschedule(string id, DateTime dueAtUtc, string ticket, string notes) => Mutate(list =>
    {
        var item = list.FirstOrDefault(t => t.Id == id && t.State == ScheduledTerminationState.Pending);
        if (item is null)
        {
            return false;
        }
        item.DueAtUtc = dueAtUtc;
        item.Ticket = ticket;
        item.Notes = notes;
        return true;
    });

    /// <summary>Atomically takes the right to offboard this entry now. Fails if it isn't pending,
    /// or another admin holds a live claim. A stale claim (see
    /// <see cref="ScheduledTerminationRules.ClaimExpiresAfter"/>) can be taken over.</summary>
    public bool TryClaim(string id, string me, DateTime nowUtc) => Mutate(list =>
    {
        var item = list.FirstOrDefault(t => t.Id == id);
        if (item is null || item.State != ScheduledTerminationState.Pending
            || ScheduledTerminationRules.IsClaimedByOther(item, me, nowUtc))
        {
            return false;
        }
        item.ClaimedBy = me;
        item.ClaimedAtUtc = nowUtc;
        return true;
    });

    /// <summary>Gives the claim back (tool never ran, was cancelled, or couldn't start). Only the
    /// claim's owner can release it.</summary>
    public void ReleaseClaim(string id, string me) => Mutate(list =>
    {
        var item = list.FirstOrDefault(t => t.Id == id);
        if (item is not null && string.Equals(item.ClaimedBy, me, StringComparison.OrdinalIgnoreCase))
        {
            item.ClaimedBy = "";
            item.ClaimedAtUtc = null;
        }
        return true;
    });

    public bool Complete(string id, string me, DateTime nowUtc, string note) => Resolve(id, ScheduledTerminationState.Completed, me, nowUtc, note);

    public bool Cancel(string id, string me, DateTime nowUtc, string note) => Resolve(id, ScheduledTerminationState.Cancelled, me, nowUtc, note);

    /// <summary>Marks the user's pending entry (if any) completed. Used when the offboarding tool
    /// finished a real run for that user, however it was launched. Returns the entry resolved.</summary>
    public ScheduledTermination? CompletePendingForUser(string username, string me, DateTime nowUtc, string note) => Mutate(list =>
    {
        var item = list.FirstOrDefault(t => t.State == ScheduledTerminationState.Pending
                                            && string.Equals(t.Username, username, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            return null;
        }
        Stamp(item, ScheduledTerminationState.Completed, me, nowUtc, note);
        return item;
    });

    private bool Resolve(string id, ScheduledTerminationState state, string me, DateTime nowUtc, string note) => Mutate(list =>
    {
        var item = list.FirstOrDefault(t => t.Id == id && t.State == ScheduledTerminationState.Pending);
        if (item is null)
        {
            return false;
        }
        Stamp(item, state, me, nowUtc, note);
        return true;
    });

    private static void Stamp(ScheduledTermination item, ScheduledTerminationState state, string me, DateTime nowUtc, string note)
    {
        item.State = state;
        item.ResolvedBy = me;
        item.ResolvedAtUtc = nowUtc;
        item.ResolutionNote = note;
        item.ClaimedBy = "";
        item.ClaimedAtUtc = null;
    }

    private T Mutate<T>(Func<List<ScheduledTermination>, T> change)
    {
        var handle = _lock.Acquire() ?? throw new IOException(
            "Could not lock the scheduled-terminations file - another session is saving. Try again in a moment.");
        try
        {
            var list = ReadAll().ToList();
            var result = change(list);
            var cutoff = DateTime.UtcNow - ScheduledTerminationRules.ResolvedHistoryKept;
            list.RemoveAll(t => t.State != ScheduledTerminationState.Pending && (t.ResolvedAtUtc ?? DateTime.UtcNow) < cutoff);
            Write(list);
            return result;
        }
        finally
        {
            _lock.Release(handle);
        }
    }

    private List<ScheduledTermination> ReadAll()
    {
        if (!File.Exists(_path))
        {
            return [];
        }
        var json = File.ReadAllText(_path);
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }
        try
        {
            return JsonSerializer.Deserialize<List<ScheduledTermination>>(json.TrimStart('﻿'), JsonOptions) ?? [];
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"The scheduled-terminations file '{_path}' is unreadable: {ex.Message}", ex);
        }
    }

    private void Write(List<ScheduledTermination> list)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(list, JsonOptions));
        File.Move(temp, _path, overwrite: true);
    }
}
