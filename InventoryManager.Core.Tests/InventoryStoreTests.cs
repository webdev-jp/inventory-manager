using InventoryManager.Core;
using InventoryManager.Core.Models;
using InventoryManager.Core.Persistence;

namespace InventoryManager.Core.Tests;

public class InventoryStoreTests : IDisposable
{
    private readonly string _dataFile;

    public InventoryStoreTests()
    {
        _dataFile = Path.Combine(Path.GetTempPath(), $"invtest_{Guid.NewGuid():N}.json");
    }

    public void Dispose()
    {
        foreach (var path in new[] { _dataFile, _dataFile + ".tmp", _dataFile + ".lock" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static InventoryItem MakeItem(int id, string deviceName, string status = "Current")
    {
        var item = new InventoryItem { Id = id, Status = status, DeviceName = deviceName };
        return item;
    }

    [Fact]
    public void Load_CraftedNonIntegerId_DoesNotThrow_AndPreservesGoodRows()
    {
        // A poisoned or corrupt inventory.json (a shared file any co-editor can touch) previously
        // crashed the whole app on load/save because Id was read with GetValue<int>(), which
        // throws on a string/bool/over-range value. It must now degrade gracefully instead.
        File.WriteAllText(_dataFile,
            "[{\"Id\":\"=cmd|'/c calc'!A1\",\"Status\":\"Current\",\"DeviceName\":\"EVIL\"}," +
            "{\"Id\":7,\"Status\":\"Current\",\"DeviceName\":\"GOOD-7\"}]");

        var store = new InventoryStore(_dataFile);
        var ex = Record.Exception(() => store.Load());
        Assert.Null(ex);
        Assert.Contains(store.Items, i => i.DeviceName == "GOOD-7" && i.Id == 7);

        // A save must also not throw, and must not drop the good row.
        Assert.Equal(InventorySaveResult.Success, store.Save());
        var reloaded = new InventoryStore(_dataFile);
        reloaded.Load();
        Assert.Contains(reloaded.Items, i => i.DeviceName == "GOOD-7");
    }

    [Theory]
    [InlineData("Terminated", "DisabledDevices")]
    [InlineData("Unknown", "Current")]
    [InlineData("NotInAD", "Current")]
    public void Load_MigratesRemovedStatuses_ToTheirNewHome(string removedStatus, string expectedStatus)
    {
        // The Terminated/Unknown/User Not in AD tabs were removed 2026-09-23 - any row a previous
        // build already parked in one of those statuses must not just silently vanish from every
        // tab the next time this file is loaded, on whatever admin's machine happens to load it
        // first.
        File.WriteAllText(_dataFile, $"[{{\"Id\":1,\"Status\":\"{removedStatus}\",\"DeviceName\":\"DEV-1\"}}]");

        var store = new InventoryStore(_dataFile);
        store.Load();

        Assert.Equal(expectedStatus, Assert.Single(store.Items).Status);

        // Not just an in-memory patch - the migration must persist to disk immediately so every
        // other session (and the raw file itself) converges, not just this one process's view.
        var reloaded = new InventoryStore(_dataFile);
        reloaded.Load();
        Assert.Equal(expectedStatus, Assert.Single(reloaded.Items).Status);
    }

    [Fact]
    public void Load_NoRemovedStatusesPresent_DoesNotRewriteTheFile()
    {
        File.WriteAllText(_dataFile, "[{\"Id\":1,\"Status\":\"Current\",\"DeviceName\":\"DEV-1\"}]");
        var writeTimeBefore = File.GetLastWriteTimeUtc(_dataFile);

        var store = new InventoryStore(_dataFile);
        store.Load();

        Assert.Equal("Current", Assert.Single(store.Items).Status);
        Assert.Equal(writeTimeBefore, File.GetLastWriteTimeUtc(_dataFile));
    }

    [Fact]
    public void Save_Then_Load_RoundTrips_AllFields()
    {
        var store = new InventoryStore(_dataFile);
        store.Load(); // no file yet - should start empty, not throw
        Assert.Empty(store.Items);

        var item = MakeItem(1, "LAPTOP-01");
        item["Model"] = "Latitude 5410";
        item["Domain"] = "CONTOSO";
        item.FirstName = "Jane";
        item.LastName = "Doe";
        store.Items.Add(item);

        var result = store.Save();
        Assert.Equal(InventorySaveResult.Success, result);

        var reloaded = new InventoryStore(_dataFile);
        reloaded.Load();
        var loadedItem = Assert.Single(reloaded.Items);
        Assert.Equal(1, loadedItem.Id);
        Assert.Equal("LAPTOP-01", loadedItem.DeviceName);
        Assert.Equal("Latitude 5410", loadedItem["Model"]);
        Assert.Equal("CONTOSO", loadedItem["Domain"]);
        Assert.Equal("Jane", loadedItem.FirstName);
        Assert.Equal("Doe", loadedItem.LastName);
    }

    [Fact]
    public void Save_Then_Load_RoundTrips_EditLockFields()
    {
        // RecordEditLock's whole cross-session visibility depends on these two fields surviving
        // the normal save/merge/reload pipeline exactly like every other special column.
        var store = new InventoryStore(_dataFile);
        store.Load();
        var item = MakeItem(1, "LAPTOP-01");
        item.EditLockedBy = "su.admin1";
        item.EditLockedAtUtc = "2026-09-15T12:00:00.0000000Z";
        store.Items.Add(item);
        store.Save();

        var reloaded = new InventoryStore(_dataFile);
        reloaded.Load();
        var loadedItem = Assert.Single(reloaded.Items);
        Assert.Equal("su.admin1", loadedItem.EditLockedBy);
        Assert.Equal("2026-09-15T12:00:00.0000000Z", loadedItem.EditLockedAtUtc);
    }

    [Fact]
    public void Save_FromTwoSeparateSessions_MergesBothAdds()
    {
        // Simulates two admins running the app at once, each adding a different device, saving
        // around the same time - the exact scenario the PowerShell build's snapshot-diff merge
        // was written to handle instead of a blind overwrite.
        var sessionA = new InventoryStore(_dataFile);
        sessionA.Load();
        sessionA.Items.Add(MakeItem(1, "DEVICE-A"));
        Assert.Equal(InventorySaveResult.Success, sessionA.Save());

        var sessionB = new InventoryStore(_dataFile);
        sessionB.Load(); // sees DEVICE-A already
        sessionB.Items.Add(MakeItem(2, "DEVICE-B"));
        Assert.Equal(InventorySaveResult.Success, sessionB.Save());

        // Session A never reloaded, so its in-memory snapshot predates DEVICE-B - saving again
        // (e.g. an unrelated edit) must not erase what session B already wrote.
        sessionA.Items[0]["Model"] = "Updated by A";
        Assert.Equal(InventorySaveResult.Success, sessionA.Save());

        var verify = new InventoryStore(_dataFile);
        verify.Load();
        Assert.Equal(2, verify.Items.Count);
        Assert.Contains(verify.Items, i => i.DeviceName == "DEVICE-A" && i["Model"] == "Updated by A");
        Assert.Contains(verify.Items, i => i.DeviceName == "DEVICE-B");
    }

    [Fact]
    public void RecordEditLock_AcquiredByOneSession_IsVisibleAndBlockingToAnother()
    {
        // The exact scenario Francois described: su.admin1 opens "laptop01" for editing while
        // su.admin2 is running a separate session against the same shared inventory.json -
        // su.admin2's session must see the lock (via a fresh Load of the same on-disk file,
        // never in-memory state shared between the two InventoryStore instances) and be refused.
        var sessionA = new InventoryStore(_dataFile);
        sessionA.Load();
        sessionA.Items.Add(MakeItem(1, "laptop01"));
        sessionA.Save();

        var laptop01InA = sessionA.Items.Single(i => i.DeviceName == "laptop01");
        RecordEditLock.Acquire(laptop01InA, "su.admin1", DateTime.UtcNow);
        Assert.Equal(InventorySaveResult.Success, sessionA.Save());

        var sessionB = new InventoryStore(_dataFile);
        sessionB.Load();
        var laptop01InB = sessionB.Items.Single(i => i.DeviceName == "laptop01");
        Assert.True(RecordEditLock.IsLockedByOther(laptop01InB, "su.admin2", DateTime.UtcNow));
        Assert.Equal("su.admin1", laptop01InB.EditLockedBy);

        // Session A finishes editing and releases - session B's NEXT load (its own periodic
        // refresh in the real app) must see the record free again. Re-fetch from sessionA.Items
        // first: Save() reloads internally and replaces every item object, so the "laptop01InA"
        // reference acquired above is now a detached, stale copy - mutating it further would
        // silently do nothing, exactly the gotcha MainForm.TryAcquireEditLock/ReleaseEditLock
        // guard against in the real app by always re-fetching by Id after each Save() call.
        var laptop01InACurrent = sessionA.Items.Single(i => i.DeviceName == "laptop01");
        RecordEditLock.Release(laptop01InACurrent, "su.admin1");
        sessionA.Save();

        var sessionBAfterRelease = new InventoryStore(_dataFile);
        sessionBAfterRelease.Load();
        var laptop01AfterRelease = sessionBAfterRelease.Items.Single(i => i.DeviceName == "laptop01");
        Assert.False(RecordEditLock.IsLockedByOther(laptop01AfterRelease, "su.admin2", DateTime.UtcNow));
        Assert.Equal("", laptop01AfterRelease.EditLockedBy);
    }

    [Fact]
    public void Save_DeletionWins_OverConcurrentEditToSameItem()
    {
        var sessionA = new InventoryStore(_dataFile);
        sessionA.Load();
        sessionA.Items.Add(MakeItem(1, "SHARED-DEVICE"));
        Assert.Equal(InventorySaveResult.Success, sessionA.Save());

        var sessionB = new InventoryStore(_dataFile);
        sessionB.Load();

        // Session A deletes the item locally (without saving yet)...
        sessionA.Items.RemoveAll(i => i.Id == 1);

        // ...meanwhile session B edits the same item and saves first.
        sessionB.Items[0]["Model"] = "Edited by B";
        Assert.Equal(InventorySaveResult.Success, sessionB.Save());

        // Session A's deletion should still win when it saves - safer than silently
        // resurrecting an item someone intentionally removed.
        Assert.Equal(InventorySaveResult.Success, sessionA.Save());

        var verify = new InventoryStore(_dataFile);
        verify.Load();
        Assert.Empty(verify.Items);
    }

    [Fact]
    public void Save_UnrelatedItem_DoesNotTouchOthersEdit()
    {
        var sessionA = new InventoryStore(_dataFile);
        sessionA.Load();
        sessionA.Items.Add(MakeItem(1, "DEVICE-A"));
        sessionA.Items.Add(MakeItem(2, "DEVICE-B"));
        Assert.Equal(InventorySaveResult.Success, sessionA.Save());

        var sessionB = new InventoryStore(_dataFile);
        sessionB.Load();

        // Session A edits DEVICE-A and saves.
        sessionA.Items.First(i => i.Id == 1)["Model"] = "Changed by A";
        Assert.Equal(InventorySaveResult.Success, sessionA.Save());

        // Session B never touched DEVICE-A, but its own snapshot still reflects the OLD
        // DEVICE-A value - saving an edit to DEVICE-B must not clobber A's change.
        sessionB.Items.First(i => i.Id == 2)["Model"] = "Changed by B";
        Assert.Equal(InventorySaveResult.Success, sessionB.Save());

        var verify = new InventoryStore(_dataFile);
        verify.Load();
        Assert.Equal("Changed by A", verify.Items.First(i => i.Id == 1)["Model"]);
        Assert.Equal("Changed by B", verify.Items.First(i => i.Id == 2)["Model"]);
    }

    [Fact]
    public void Save_WhenLockAlreadyHeldAndFresh_TimesOutWithoutCorruptingFile()
    {
        var store = new InventoryStore(_dataFile, new InventoryLock(_dataFile + ".lock", timeout: TimeSpan.FromMilliseconds(300), staleAfter: TimeSpan.FromHours(1)));
        store.Load();
        store.Items.Add(MakeItem(1, "DEVICE-A"));
        Assert.Equal(InventorySaveResult.Success, store.Save());

        // Simulate another session mid-save: a fresh lock file that is NOT stale yet.
        using (new FileStream(_dataFile + ".lock", FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            store.Items[0]["Model"] = "Should not persist yet";
            var result = store.Save();
            Assert.Equal(InventorySaveResult.LockTimedOut, result);
        }

        File.Delete(_dataFile + ".lock");
        var verify = new InventoryStore(_dataFile);
        verify.Load();
        // The original save (before the simulated lock) must still be intact and uncorrupted.
        Assert.Equal("DEVICE-A", verify.Items.Single().DeviceName);
        Assert.Equal("", verify.Items.Single()["Model"]);
    }

    [Fact]
    public void Load_ToleratesRealPowerShellJsonShape()
    {
        // A minimal but realistic slice of what the PowerShell build's ConvertTo-Json actually
        // produces, including the two-space-after-colon formatting - confirms the C# reader is
        // compatible with a real inventory.json, not just its own round-tripped output.
        var json = """
        [
            {
                "Id":  1,
                "Status":  "Current",
                "ADFlagReason":  "",
                "ADComputerDisabled":  "",
                "ADComputerNotFound":  "",
                "IgnoreHighlight":  "",
                "DeviceName":  "360-L03",
                "LastName":  "Smith",
                "FirstName":  "John",
                "FormerUser":  "",
                "Model":  "Latitude 5410",
                "SerialNumber":  "DM0Q163",
                "Domain":  "CONTOSO",
                "ESET":  "Installed",
                "AdminProfile":  "CorpAdmin",
                "Splashtop":  "Yes",
                "Docking":  "No",
                "SecondMonitor":  "No",
                "PortableMonitor":  "No",
                "Topaz":  "No",
                "CellPhone":  "",
                "DeviceModel":  "Latitude 5410",
                "Description":  "John Smith***",
                "Username":  "smithj"
            }
        ]
        """;
        File.WriteAllText(_dataFile, json);

        var store = new InventoryStore(_dataFile);
        store.Load();

        var item = Assert.Single(store.Items);
        Assert.Equal(1, item.Id);
        Assert.Equal("Current", item.Status);
        Assert.Equal("360-L03", item.DeviceName);
        Assert.Equal("smithj", item.Username);
        Assert.Equal("John Smith***", item.Description);
        Assert.Equal("Latitude 5410", item["Model"]);
    }

    [Fact]
    public void Load_MissingStatus_DefaultsToCurrent()
    {
        File.WriteAllText(_dataFile, """[{"Id": 1, "DeviceName": "X"}]""");
        var store = new InventoryStore(_dataFile);
        store.Load();
        Assert.Equal("Current", store.Items.Single().Status);
    }

    [Fact]
    public void RestoreFromContent_ReplacesFileAndReloads()
    {
        var store = new InventoryStore(_dataFile);
        store.Load();
        store.Items.Add(MakeItem(1, "CURRENT-DEVICE"));
        store.Save();

        var backupContent = """[{"Id": 99, "DeviceName": "RESTORED-DEVICE", "Status": "Current"}]""";
        var result = store.RestoreFromContent(backupContent);

        Assert.Equal(InventorySaveResult.Success, result);
        var restoredItem = Assert.Single(store.Items);
        Assert.Equal(99, restoredItem.Id);
        Assert.Equal("RESTORED-DEVICE", restoredItem.DeviceName);
    }

    [Fact]
    public void Save_FieldChanged_RecordsFieldChangeWithOldAndNewValue()
    {
        var store = new InventoryStore(_dataFile);
        store.Load();
        var item = MakeItem(1, "LAPTOP-01");
        item.Username = "jsmith";
        store.Items.Add(item);
        store.Save();

        var current = store.Items.Single();
        current["CellPhone"] = "914-555-0100";
        store.Save();

        var change = Assert.Single(store.LastSaveFieldChanges);
        Assert.Equal(1, change.ItemId);
        Assert.Equal("LAPTOP-01", change.DeviceName);
        Assert.Equal("jsmith", change.Username);
        Assert.Equal("CellPhone", change.FieldKey);
        Assert.Equal("Cellphone #", change.FieldLabel);
        Assert.Equal("", change.OldValue);
        Assert.Equal("914-555-0100", change.NewValue);
    }

    [Fact]
    public void Save_DescriptionChanged_RecordsFieldKeyDescription()
    {
        // FieldKey is what MainForm.RecordFieldChangeAudits keys off of to auto-push a changed
        // Description to the AD computer object (Francois, 2026-09-16) - a wrong/missing key here
        // would silently break that sync without any test noticing via FieldLabel alone.
        var store = new InventoryStore(_dataFile);
        store.Load();
        store.Items.Add(MakeItem(1, "LAPTOP-01"));
        store.Save();

        var current = store.Items.Single();
        current.Description = "New description";
        store.Save();

        var change = Assert.Single(store.LastSaveFieldChanges);
        Assert.Equal("Description", change.FieldKey);
        Assert.Equal("Description", change.FieldLabel);
        Assert.Equal("New description", change.NewValue);
    }

    [Fact]
    public void Save_StatusChanged_RecordsFieldKeyStatus()
    {
        var store = new InventoryStore(_dataFile);
        store.Load();
        store.Items.Add(MakeItem(1, "LAPTOP-01", status: "Current"));
        store.Save();

        var current = store.Items.Single();
        current.Status = "ITStock";
        store.Save();

        var change = Assert.Single(store.LastSaveFieldChanges);
        Assert.Equal("Status", change.FieldKey);
        Assert.Equal("Status", change.FieldLabel);
    }

    [Fact]
    public void Save_MultipleFieldsChanged_RecordsEachSeparately()
    {
        var store = new InventoryStore(_dataFile);
        store.Load();
        store.Items.Add(MakeItem(1, "LAPTOP-01"));
        store.Save();

        var current = store.Items.Single();
        current["CellPhone"] = "914-555-0100";
        current["Model"] = "Latitude 5420";
        store.Save();

        Assert.Equal(2, store.LastSaveFieldChanges.Count);
        Assert.Contains(store.LastSaveFieldChanges, c => c.FieldLabel == "Cellphone #" && c.NewValue == "914-555-0100");
        Assert.Contains(store.LastSaveFieldChanges, c => c.FieldLabel == "Model" && c.NewValue == "Latitude 5420");
    }

    [Fact]
    public void Save_NoFieldsChanged_RecordsNoFieldChanges()
    {
        var store = new InventoryStore(_dataFile);
        store.Load();
        store.Items.Add(MakeItem(1, "LAPTOP-01"));
        store.Save();

        // A save where nothing in the in-memory list changed at all - changedIds is empty, so
        // there's nothing to diff regardless.
        store.Save();

        Assert.Empty(store.LastSaveFieldChanges);
    }

    [Fact]
    public void Save_NewItemAdded_DoesNotRecordFieldChanges()
    {
        var store = new InventoryStore(_dataFile);
        store.Load();
        store.Items.Add(MakeItem(1, "BRAND-NEW"));
        store.Save();

        // A brand-new item is a creation, not a "modification" - every field going from "" to a
        // real value shouldn't flood the audit trail as N field changes.
        Assert.Empty(store.LastSaveFieldChanges);
    }

    [Fact]
    public void Save_StatusChanged_RecordsAsFieldChange()
    {
        var store = new InventoryStore(_dataFile);
        store.Load();
        store.Items.Add(MakeItem(1, "LAPTOP-01", status: "Current"));
        store.Save();

        store.Items.Single().Status = "ITStock";
        store.Save();

        var change = Assert.Single(store.LastSaveFieldChanges);
        Assert.Equal("Status", change.FieldLabel);
        Assert.Equal("Current", change.OldValue);
        Assert.Equal("ITStock", change.NewValue);
    }

    [Fact]
    public void Save_OnlyEditLockFieldsChanged_DoesNotRecordFieldChanges()
    {
        var store = new InventoryStore(_dataFile);
        store.Load();
        store.Items.Add(MakeItem(1, "LAPTOP-01"));
        store.Save();

        // Acquiring the per-record edit lock changes EditLockedBy/EditLockedAtUtc, which must
        // never be reported as a "field change" - it would spam the audit trail on every single
        // Edit/Transfer/Offboard open and close.
        store.Items.Single().EditLockedBy = "suadmin";
        store.Items.Single().EditLockedAtUtc = DateTime.UtcNow.ToString("o");
        store.Save();

        Assert.Empty(store.LastSaveFieldChanges);
    }

    [Fact]
    public void Save_OnlyAdSyncFlagsChanged_DoesNotRecordFieldChanges()
    {
        var store = new InventoryStore(_dataFile);
        store.Load();
        store.Items.Add(MakeItem(1, "LAPTOP-01"));
        store.Save();

        // ADFlagReason/ADComputerDisabled/ADComputerNotFound/IgnoreHighlight/IgnoreAdSync are
        // system/AD-Sync-owned bookkeeping, not a deliberate field edit - excluded from the audit
        // diff by design.
        var current = store.Items.Single();
        current.ADFlagReason = "AD account disabled";
        current.ADComputerDisabled = "Computer object disabled in AD";
        current.ADComputerNotFound = "No matching AD computer object found";
        current.IgnoreHighlight = "Yes";
        current.IgnoreAdSync = "Yes";
        store.Save();

        Assert.Empty(store.LastSaveFieldChanges);
    }

    [Fact]
    public void Save_MultipleItemsChangedInOneSave_RecordsChangesForEach()
    {
        var store = new InventoryStore(_dataFile);
        store.Load();
        store.Items.Add(MakeItem(1, "LAPTOP-01"));
        store.Items.Add(MakeItem(2, "LAPTOP-02"));
        store.Save();

        store.Items.Single(i => i.DeviceName == "LAPTOP-01")["Model"] = "Latitude 5420";
        store.Items.Single(i => i.DeviceName == "LAPTOP-02")["Model"] = "Latitude 5430";
        store.Save();

        Assert.Equal(2, store.LastSaveFieldChanges.Count);
        Assert.Contains(store.LastSaveFieldChanges, c => c.ItemId == 1 && c.NewValue == "Latitude 5420");
        Assert.Contains(store.LastSaveFieldChanges, c => c.ItemId == 2 && c.NewValue == "Latitude 5430");
    }

    [Fact]
    public void Save_UsernameClearedInSameSaveAsOtherFieldChanges_StillAttributesToThePriorUsername()
    {
        // Regression for a real bug found live 2026-09-16: Offboarding's own inventory-sync step
        // clears Username in the SAME save that also flips Status to Terminated and updates
        // Description. Every InventoryFieldChange from that save must still be attributed to the
        // account the record belonged to ("jsmith") - not "" - or MainForm's AD-Notes mirror
        // silently has nothing to write to for the very save documenting what happened to that
        // account.
        var store = new InventoryStore(_dataFile);
        store.Load();
        var item = MakeItem(1, "LAPTOP-01");
        item["Username"] = "jsmith";
        item["Description"] = "";
        store.Items.Add(item);
        store.Save();

        var current = store.Items.Single(i => i.Id == 1);
        current["Username"] = "";
        current["Description"] = "Disabled per ticket # 12345";
        current.Status = "ITStock";
        store.Save();

        Assert.NotEmpty(store.LastSaveFieldChanges);
        Assert.All(store.LastSaveFieldChanges, c => Assert.Equal("jsmith", c.Username));
    }
}
