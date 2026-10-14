using System.Text.Json;
using System.Text.Json.Nodes;
using InventoryManager.Core.Models;

namespace InventoryManager.Core.Persistence;

/// <summary>
/// Direct port of the PowerShell build's Load-Inventory / Save-Inventory / Read-InventoryFileRaw
/// / Get-InventorySnapshotMap / ConvertTo-InventoryRowObject. Multi-user-safe save: merges only
/// THIS instance's own added/changed/deleted items (found by diffing the in-memory list against
/// the snapshot taken at its last Load/Save) onto whatever is CURRENTLY on disk, under a
/// cross-machine lock - never a blind full-file overwrite. That means two people saving different
/// items around the same time both keep their changes, instead of whoever saves last silently
/// erasing the other's edit.
///
/// JSON shape is intentionally identical to the PowerShell build's inventory.json (same property
/// names/casing, same special-column set, same per-item flat object) so a copy of a real
/// inventory.json can be loaded here directly during the rewrite's parity period.
/// </summary>
public sealed class InventoryStore
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public string DataFilePath { get; }

    private readonly InventoryLock _lock;
    private Dictionary<int, string> _lastSyncSnapshot = new();
    private Dictionary<int, InventoryItem> _lastLoadedItems = new();

    public List<InventoryItem> Items { get; private set; } = [];

    /// <summary>Field-level changes detected by the most recent Save() call, for items that
    /// already existed (never populated for a brand-new item - that's a creation, not a
    /// "modification"). Feeds the per-field audit trail (local tamper-evident log + AD Notes
    /// mirror) - see MainForm.RecordFieldChangeAudits.</summary>
    public IReadOnlyList<InventoryFieldChange> LastSaveFieldChanges { get; private set; } = [];

    public InventoryStore(string dataFilePath, InventoryLock? inventoryLock = null)
    {
        DataFilePath = dataFilePath;
        _lock = inventoryLock ?? new InventoryLock(dataFilePath + ".lock");
    }

    public void Load()
    {
        var items = new List<InventoryItem>();
        foreach (var obj in ReadRawFromDisk())
        {
            items.Add(FromJsonObject(obj));
        }

        // Baselines must be captured from on-disk truth BEFORE MigrateRemovedStatuses mutates
        // anything below - otherwise a migrated row's new Status would match its own baseline and
        // Save()'s diff would never notice it needs writing back to disk.
        _lastSyncSnapshot = BuildSnapshotMap(items);
        // Full per-field baseline (a deep clone, not just the cheap joined-string snapshot above)
        // so the next Save() can report exactly WHICH field changed and what it changed from/to,
        // not just "this item's combined state differs."
        _lastLoadedItems = items.ToDictionary(i => i.Id, i => i.Clone());

        var migratedAny = MigrateRemovedStatuses(items);
        Items = items;

        if (migratedAny)
        {
            // A real state transition, not a transient in-memory patch - persist it immediately so
            // every other admin's session (and the raw JSON file itself) converges on the new
            // status the next time anyone loads or saves, rather than re-discovering the same
            // stale rows on every future Load() forever. Goes through the normal Core-only Save()
            // path - no AD write, no App-layer audit mirror (see RecordFieldChangeAudits in
            // MainForm) - this is a schema upgrade, not a user-driven edit.
            Save();
        }
    }

    /// <summary>One-time upgrade for rows still carrying a Status this schema no longer defines.
    /// Francois removed the Terminated/Unknown/User Not in AD tabs 2026-09-23 ("remove anything and
    /// everything that attaches itself to those tabs") and asked specifically that a terminated
    /// device land in Disabled Devices with its AD Notes updated (now handled at offboarding time by
    /// MainForm.OnOffboardingToolClosed - see there for the new-offboard path). This is the
    /// complementary one-time fix for rows that were ALREADY in one of the three removed statuses
    /// before this schema change shipped, on any admin's machine, at whatever moment their app
    /// happens to load the shared file next: Terminated folds into Disabled Devices (same
    /// destination the live offboarding flow now uses); Unknown and User Not in AD both fold into
    /// Current Inventory (their most likely true resting tab - AD Sync/Check AD will simply
    /// re-flag/re-classify them on the next run same as any other Current row, and a User Not in
    /// AD row's ADFlagReason is untouched by this method, so its red highlight survives the move
    /// unchanged). Idempotent - a row already on a still-valid status is left alone, so this is a
    /// no-op on every load after the first one that actually finds something to migrate.</summary>
    private static bool MigrateRemovedStatuses(List<InventoryItem> items)
    {
        var migrated = false;
        foreach (var item in items)
        {
            var newStatus = item.Status switch
            {
                "Terminated" => "DisabledDevices",
                "Unknown" => "Current",
                "NotInAD" => "Current",
                _ => null,
            };
            if (newStatus is not null)
            {
                item.Status = newStatus;
                migrated = true;
            }
        }
        return migrated;
    }

    public InventorySaveResult Save()
    {
        var lockHandle = _lock.Acquire();
        if (lockHandle is null)
        {
            return InventorySaveResult.LockTimedOut;
        }
        try
        {
            var localSnapshot = BuildSnapshotMap(Items);
            var priorSnapshot = _lastSyncSnapshot;

            var changedIds = localSnapshot.Keys
                .Where(id => !priorSnapshot.TryGetValue(id, out var prior) || prior != localSnapshot[id])
                .ToList();
            var deletedIds = priorSnapshot.Keys
                .Where(id => !localSnapshot.ContainsKey(id))
                .ToList();

            LastSaveFieldChanges = ComputeFieldChanges(changedIds);

            var diskMap = new Dictionary<int, JsonObject>();
            // Rows whose Id isn't a usable integer (a corrupt or maliciously hand-edited
            // inventory.json - e.g. "Id":"=cmd|'/c calc'!A1", a bool, or an out-of-range number)
            // are never coerced to int here. The previous GetId did obj.GetValue<int>(), which
            // THREW on any such value and crashed the whole save/load for every session sharing
            // the file. They're now carried through verbatim so a single poisoned row can neither
            // crash the app nor silently drop legitimate data.
            var passthrough = new List<JsonObject>();
            foreach (var obj in ReadRawFromDisk())
            {
                if (TryGetId(obj, out var id))
                {
                    diskMap[id] = obj;
                }
                else
                {
                    passthrough.Add(obj);
                }
            }

            foreach (var id in changedIds)
            {
                var localItem = Items.FirstOrDefault(i => i.Id == id);
                // An item this session both added AND then deleted again before ever saving
                // won't be found - nothing to merge in, which is correct.
                if (localItem is not null)
                {
                    diskMap[id] = ToJsonObject(localItem);
                }
            }
            foreach (var id in deletedIds)
            {
                // If someone else's concurrent edit to this same item is sitting on disk and this
                // session deleted it locally, the deletion wins - safer than silently
                // resurrecting an item someone intentionally removed.
                diskMap.Remove(id);
            }

            var merged = new JsonArray(diskMap
                .OrderBy(kv => kv.Key)
                .Select(kv => (JsonNode)kv.Value.DeepClone())
                .Concat(passthrough.Select(o => (JsonNode)o.DeepClone()))
                .ToArray());

            var tempPath = DataFilePath + ".tmp";
            File.WriteAllText(tempPath, merged.ToJsonString(WriteOptions));
            // Write-then-move, not a direct write to DataFilePath - a reader (another session's
            // Load, or this one's own periodic refresh) can never see a half-written file.
            File.Move(tempPath, DataFilePath, overwrite: true);
        }
        finally
        {
            _lock.Release(lockHandle);
        }

        // Reload from the merged, now-canonical file - this also picks up anything OTHER
        // sessions added/changed since this instance's own last load, not just what this save
        // just wrote.
        Load();
        return InventorySaveResult.Success;
    }

    /// <summary>
    /// Port of Show-RestoreBackupDialog's restore step: a real, intentional full-file replacement
    /// (unlike Save's per-row merge), still under the same cross-machine lock so it can't land in
    /// the middle of another session's own save. Caller is responsible for backing up the current
    /// content first (see BackupManager.RunPeriodicCheck) - this method only performs the swap.
    /// </summary>
    public InventorySaveResult RestoreFromContent(string content)
    {
        var lockHandle = _lock.Acquire();
        if (lockHandle is null)
        {
            return InventorySaveResult.LockTimedOut;
        }
        try
        {
            File.WriteAllText(DataFilePath, content);
        }
        finally
        {
            _lock.Release(lockHandle);
        }
        Load();
        return InventorySaveResult.Success;
    }

    private List<JsonObject> ReadRawFromDisk()
    {
        if (!File.Exists(DataFilePath))
        {
            return [];
        }
        var json = File.ReadAllText(DataFilePath);
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }
        var node = JsonNode.Parse(json);
        return node switch
        {
            JsonArray array => array.OfType<JsonObject>().ToList(),
            JsonObject single => [single],
            _ => [],
        };
    }

    /// <summary>Reads a usable integer Id from a raw JSON object without ever throwing on bad
    /// input: accepts a JSON number or a numeric string, and reports failure (rather than
    /// crashing) for anything else, so one malformed row can't take down load/save.</summary>
    private static bool TryGetId(JsonObject obj, out int id)
    {
        id = 0;
        if (obj.TryGetPropertyValue("Id", out var node) && node is JsonValue value)
        {
            if (value.TryGetValue<int>(out var asInt))
            {
                id = asInt;
                return true;
            }
            if (value.TryGetValue<string>(out var asString) && int.TryParse(asString, out var parsed))
            {
                id = parsed;
                return true;
            }
        }
        return false;
    }

    private static JsonObject ToJsonObject(InventoryItem item)
    {
        var obj = new JsonObject
        {
            ["Id"] = item.Id,
            ["Status"] = item.Status,
            ["ADFlagReason"] = item.ADFlagReason,
            ["ADComputerDisabled"] = item.ADComputerDisabled,
            ["ADComputerNotFound"] = item.ADComputerNotFound,
            ["IgnoreHighlight"] = item.IgnoreHighlight,
            ["IgnoreAdSync"] = item.IgnoreAdSync,
            ["EditLockedBy"] = item.EditLockedBy,
            ["EditLockedAtUtc"] = item.EditLockedAtUtc,
        };
        foreach (var field in InventorySchema.FieldDefs)
        {
            obj[field.Key] = item[field.Key];
        }
        return obj;
    }

    private static InventoryItem FromJsonObject(JsonObject obj)
    {
        string GetString(string key, string fallback = "")
        {
            if (obj.TryGetPropertyValue(key, out var node) && node is not null)
            {
                return node.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : node.ToString();
            }
            return fallback;
        }

        var item = new InventoryItem
        {
            Id = TryGetId(obj, out var parsedId) ? parsedId : 0,
            Status = string.IsNullOrWhiteSpace(GetString("Status")) ? "Current" : GetString("Status"),
            ADFlagReason = GetString("ADFlagReason"),
            ADComputerDisabled = GetString("ADComputerDisabled"),
            ADComputerNotFound = GetString("ADComputerNotFound"),
            IgnoreHighlight = GetString("IgnoreHighlight"),
            IgnoreAdSync = GetString("IgnoreAdSync"),
            EditLockedBy = GetString("EditLockedBy"),
            EditLockedAtUtc = GetString("EditLockedAtUtc"),
        };
        foreach (var field in InventorySchema.FieldDefs)
        {
            item[field.Key] = GetString(field.Key);
        }
        return item;
    }

    /// <summary>
    /// Field-by-field diff between each changed item's CURRENT values and its value at the last
    /// Load()/Save() (<see cref="_lastLoadedItems"/>) - the actual per-field audit detail, as
    /// opposed to BuildSnapshotMap's cheap "did anything change at all" joined string. Only ever
    /// diffs <see cref="InventorySchema.FieldDefs"/> (the real, user-editable inventory fields)
    /// plus Status (a Move) - deliberately excludes EditLockedBy/EditLockedAtUtc (the per-record
    /// edit lock's own bookkeeping, not user data - logging every lock acquire/release as a "field
    /// change" would swamp the audit trail with noise) and the AD-Sync-owned flags
    /// (ADFlagReason/ADComputerDisabled/ADComputerNotFound/IgnoreHighlight/IgnoreAdSync), which are
    /// set by an automated check (or, for IgnoreAdSync, an explicit but separate toggle action -
    /// still not a "field edit" in the audit-trail sense) rather than a deliberate field edit. A
    /// brand-new item (not present in
    /// <see cref="_lastLoadedItems"/>) is a creation, not a modification, so it's skipped entirely.
    /// </summary>
    private List<InventoryFieldChange> ComputeFieldChanges(IEnumerable<int> changedIds)
    {
        var changes = new List<InventoryFieldChange>();
        foreach (var id in changedIds)
        {
            var current = Items.FirstOrDefault(i => i.Id == id);
            if (current is null || !_lastLoadedItems.TryGetValue(id, out var prior))
            {
                continue;
            }
            // Prefer the CURRENT Username, but fall back to the prior one when this very save is
            // what just cleared it (e.g. Offboarding's own inventory sync wipes Username in the
            // same save that also flips Status to DisabledDevices) - otherwise every change in that
            // save would be attributed to a blank identity and MainForm's AD-Notes mirror would
            // silently skip writing to the account the change is actually about. A real save where
            // the device genuinely never had a linked user still falls through to "" here, same as
            // before.
            var attributedUsername = !string.IsNullOrWhiteSpace(current.Username) ? current.Username : prior.Username;
            foreach (var field in InventorySchema.FieldDefs)
            {
                var oldValue = prior[field.Key];
                var newValue = current[field.Key];
                if (oldValue != newValue)
                {
                    changes.Add(new InventoryFieldChange(id, current.DeviceName, attributedUsername, field.Key, field.Label, oldValue, newValue));
                }
            }
            if (prior.Status != current.Status)
            {
                changes.Add(new InventoryFieldChange(id, current.DeviceName, attributedUsername, "Status", "Status", prior.Status, current.Status));
            }
        }
        return changes;
    }

    /// <summary>
    /// A cheap Id -> "all field values joined" map of the current in-memory items, used to
    /// detect exactly which items THIS instance added/changed/deleted since its last Load/Save -
    /// by diffing two snapshots at save time, never by tracking edits as they happen.
    /// </summary>
    private static Dictionary<int, string> BuildSnapshotMap(IEnumerable<InventoryItem> items)
    {
        const char unitSeparator = '';
        var map = new Dictionary<int, string>();
        foreach (var item in items)
        {
            var parts = new List<string>
            {
                item.Status, item.ADFlagReason, item.ADComputerDisabled, item.ADComputerNotFound, item.IgnoreHighlight,
                item.IgnoreAdSync, item.EditLockedBy, item.EditLockedAtUtc,
            };
            parts.AddRange(InventorySchema.FieldDefs.Select(f => item[f.Key]));
            map[item.Id] = string.Join(unitSeparator, parts);
        }
        return map;
    }
}
