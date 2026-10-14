Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.DirectoryServices.AccountManagement

$script:ExePath = [System.Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
if ($PSScriptRoot) {
    $script:AppDir = $PSScriptRoot
} else {
    $script:AppDir = Split-Path -Parent $script:ExePath
}
$script:DataFile = Join-Path $script:AppDir "inventory.json"
$script:BackupDir = Join-Path $script:AppDir "Backups"
$script:MaxBackups = 5
$script:BackupIntervalMs = 5 * 60 * 1000
$script:OffboardingScriptPath = Join-Path $script:AppDir "Offboarding2026.ps1"

# ---- Multi-user concurrency ----
# inventory.json is a shared file - multiple copies of this app, on multiple machines, may have
# it open at once. $script:LockFilePath is a small marker file (NOT the data file itself) used
# to serialize the read-merge-write cycle in Save-Inventory across all of them; an exclusive
# CreateNew file-open is the one locking primitive that genuinely works across machines over a
# network share (a .NET Mutex does not - it's local-machine-only). $script:InventoryRefreshMs
# controls how often a session that ISN'T actively saving still checks for other users' changes.
$script:LockFilePath = Join-Path $script:AppDir "inventory.lock"
$script:LockStaleSeconds = 30
$script:LockTimeoutSeconds = 15
$script:InventoryRefreshMs = 15 * 1000
$script:LastSyncSnapshot = $null
$script:LastKnownFileWriteTimeUtc = $null

# Persistent log - the in-app Activity Log panel only ever lived in memory and vanished the
# moment the app closed, which makes it useless for handing over after the fact when something
# fails. One file per day, plain text, append-only.
$script:LogDir = Join-Path $script:AppDir "Logs"
$script:LogFilePath = Join-Path $script:LogDir "InventoryManager_$(Get-Date -Format 'yyyy-MM-dd').log"

# ps2exe embeds this into the compiled exe's file resources (why Explorer shows it correctly),
# but that's separate from a WinForms window's own taskbar/title-bar icon, which only ever comes
# from that window's .Icon property at runtime - never set previously, hence the generic icon on
# every window. Loaded once here and applied to each Form created below.
$script:AppIconPath = Join-Path $script:AppDir "inventory.ico"
$script:AppIcon = if (Test-Path $script:AppIconPath) {
    try { New-Object System.Drawing.Icon($script:AppIconPath) } catch { $null }
} else { $null }

# Organizational scope for AD computer objects: only computers under this OU are considered
# for existence/disabled checks (avoids noise from servers, DCs, or unmanaged objects
# elsewhere in the domain). Computers under the child OU are treated as disabled regardless
# of their Enabled attribute, matching this org's convention of moving decommissioned
# machines there.
$script:ComputersOuDn = "OU=Domain Computers,DC=contoso,DC=local"
$script:DisabledComputersOuDn = "OU=Disabled Computers,OU=Domain Computers,DC=contoso,DC=local"

# New AD accounts log in with this UPN suffix, NOT the AD domain name itself
# (contoso.local) - confirmed this is intentionally the org's email domain instead.
$script:OnboardingUpnSuffix = "contoso.com"

# Onboarding may only place a new user in a sub-OU of this base - NEVER directly in the base
# OU itself (confirmed explicitly: even "Domain Users" itself is not a valid target, only its
# children). Enforced both in Show-OuPickerDialog's candidate list and again, as a hard
# pre-flight check, right before New-ADUser runs.
$script:AllowedUserOuBaseDn = "OU=Domain Users,DC=contoso,DC=local"

# Silently marks every AD user this tool creates, for later incident-response correlation
# ("was this account provisioned through our sanctioned onboarding tool, or did it appear some
# other way") - NOT shown anywhere in this app's own UI or the standard AD Users and Computers
# tabs, only visible via the Attribute Editor tab (requires "Advanced Features" enabled in
# ADUC), so this is meant to stay known only to whoever is told about it.
# "comment" is a base AD schema attribute (present on every user object regardless of whether
# the Exchange schema extension was ever applied to this forest - confirmed this domain doesn't
# show extensionAttribute1-15 at all in the Attribute Editor, so that was the wrong choice).
$script:OnboardingMarkerAttribute = "comment"
$script:OnboardingMarkerValue = "InventoryAccountCreated"

# ---- Privileged-group deny list (onboarding security-group picker) ----
# SID-based (primary defense): matched by well-known RID, so renaming a group in AD does not
# bypass this - only the RID does. BUILTIN\* groups use the fixed S-1-5-32-<rid> form (identical
# across every domain); the others are this domain's own SID + RID.
$script:PrivilegedGroupRids = @(512, 518, 519, 520, 526, 527)   # Domain/Schema/Enterprise Admins, Group Policy Creator Owners, Key/Enterprise Key Admins
$script:PrivilegedBuiltinRids = @(544, 548, 549, 550, 551)      # Administrators, Account/Server/Print/Backup Operators

# Name-based (supplemental): a small number of highly-privileged groups do NOT have a fixed
# well-known RID - their SID is allocated normally when the relevant role (DNS, AD CS, etc.) is
# installed, so they can only be matched by name. Add any other org-specific
# "effectively Domain Admin" group here.
$script:PrivilegedGroupNames = @("DnsAdmins", "Cert Publishers")

# ---- Access gate configuration ----
# AD domain used for the two-layer login below (System.DirectoryServices.AccountManagement
# binds against this directly - it does not require the ActiveDirectory RSAT module).
$script:AdDomainFqdn = "contoso.local"

# The single dedicated "gate" account (OU=IT,OU=System Apps,OU=Domain Users,DC=contoso,DC=local).
# It should carry NO rights in AD beyond existing - this is only a "does this operator know the
# passphrase to even launch the tool" check. Real authorization is the SU group membership
# check below, not this account. Its OU location doesn't matter here - credential validation
# via PrincipalContext binds against the whole domain, not a specific OU.
$script:InventoryGateAccountSam = "inv-gateaccount"

# AD security group whose members are allowed to actually use this tool as an SU
# (checked transitively, so nested membership counts).
$script:SuGroupName = "InventoryManager"

$script:MaxLoginAttempts = 3
$script:SuUsername = $null

# ---- Field definitions (order = display order in grid and dialog) ----
# Aliases = extra CSV header names (case-insensitive) recognized on import, e.g. from
# an existing spreadsheet that doesn't use this app's exact field names/labels.
$script:FieldDefs = @(
    @{ Key = "DeviceName";      Label = "Device Name";  Type = "text";     Aliases = @("Device") }
    @{ Key = "LastName";        Label = "Last Name";    Type = "text" }
    @{ Key = "FirstName";       Label = "First Name";   Type = "text" }
    @{ Key = "FormerUser";      Label = "Former User";  Type = "text";     Aliases = @("Former User(s)"); Hidden = $true }
    @{ Key = "Model";           Label = "Model";        Type = "text" }
    @{ Key = "SerialNumber";    Label = "Serial #";     Type = "text";     Aliases = @("Serial") }
    @{ Key = "Domain";          Label = "Domain";        Type = "dropdown"; Options = @("CONTOSO", "Off-Domain");                  Default = "CONTOSO";     Aliases = @("Domain?") }
    @{ Key = "ESET";            Label = "ESET";          Type = "dropdown"; Options = @("Installed", "Not installed");          Default = "Installed"; Aliases = @("ESET on Laptop?") }
    @{ Key = "AdminProfile";    Label = "Admin Profile"; Type = "dropdown"; Options = @("CorpAdmin", "OrgAdmin", "Other");       Default = "CorpAdmin" }
    @{ Key = "Splashtop";       Label = "Splashtop";     Type = "dropdown"; Options = @("Yes", "No");                          Default = "Yes";       Aliases = @("Splashtop installed?") }
    @{ Key = "Docking";         Label = "Docking";       Type = "dropdown"; Options = @("Yes", "No");                          Default = "No" }
    @{ Key = "SecondMonitor";   Label = "2nd Monitor";   Type = "dropdown"; Options = @("Yes", "No");                          Default = "No";        Aliases = @("2nd screen") }
    @{ Key = "PortableMonitor"; Label = "Portable Mon."; Type = "dropdown"; Options = @("Yes", "No");                          Default = "No";        Aliases = @("Portable screen") }
    @{ Key = "Topaz";           Label = "Topaz";         Type = "dropdown"; Options = @("Yes", "No");                          Default = "No" }
    @{ Key = "CellPhone";       Label = "Cellphone #";  Type = "text";     Aliases = @("Wireless Number") }
    @{ Key = "DeviceModel";     Label = "Device Model"; Type = "dynamicdropdown"; Aliases = @("Mobile Device Model") }
    @{ Key = "Description";    Label = "Description";  Type = "text";     SyncToAd = $true }
    @{ Key = "Username";       Label = "Username";     Type = "text";     ReadOnly = $true }
)

# Hidden fields (currently just FormerUser) stay in $script:FieldDefs so the DataTable column,
# JSON load/save, and CSV import continue to carry their data untouched - they just drop out of
# every user-facing surface (grid, Add/Edit dialog, search, CSV export) via this filtered list.
$script:VisibleFieldDefs = @($script:FieldDefs | Where-Object { -not $_.ContainsKey("Hidden") -or -not $_.Hidden })

# ---- Tabs: every item has a Status, which controls which tab it appears on ----
$script:Statuses = @(
    @{ Key = "Current";         Label = "Current Inventory" }
    @{ Key = "Terminated";      Label = "Terminated" }
    @{ Key = "ITStock";         Label = "IT Stock" }
    @{ Key = "Unknown";         Label = "Unknown" }
    @{ Key = "DisabledDevices"; Label = "Disabled Devices" }
    @{ Key = "ComputerNotInAD"; Label = "Computer Not in AD" }
    @{ Key = "NotInAD";         Label = "User Not in AD" }
)

function Get-FieldDefault([hashtable]$field) {
    if ($field.Type -eq "dropdown") { return $field.Default }
    return ""
}

function Get-DistinctFieldValues([string]$FieldKey) {
    # Live options for a "dynamicdropdown" field - whatever values already exist in the
    # inventory right now, not a fixed list, so it grows on its own as new ones get typed in
    # (an editable combo, not a locked-down DropDownList) instead of needing a code change every
    # time a new device model shows up.
    $set = New-Object System.Collections.Generic.HashSet[string]
    foreach ($row in $script:Items.Rows) {
        $val = "$($row[$FieldKey])".Trim()
        if (-not [string]::IsNullOrWhiteSpace($val)) { [void]$set.Add($val) }
    }
    return @($set | Sort-Object)
}

$script:Items = New-Object System.Data.DataTable
[void]$script:Items.Columns.Add("Id", [int])
[void]$script:Items.Columns.Add("Status", [string])
[void]$script:Items.Columns.Add("ADFlagReason", [string])
[void]$script:Items.Columns.Add("ADComputerDisabled", [string])
[void]$script:Items.Columns.Add("ADComputerNotFound", [string])
[void]$script:Items.Columns.Add("IgnoreHighlight", [string])
# Computed-only columns for the duplicate-device grouping display (never saved to inventory.json -
# recomputed from DeviceName + $script:ExpandedGroups every time the data changes).
[void]$script:Items.Columns.Add("GroupCollapsed", [string])
[void]$script:Items.Columns.Add("GroupPrimary", [string])
[void]$script:Items.Columns.Add("GroupCount", [string])
foreach ($f in $script:FieldDefs) {
    [void]$script:Items.Columns.Add($f.Key, [string])
}

function Get-NextId {
    if ($script:Items.Rows.Count -eq 0) { return 1 }
    return (($script:Items.Rows | ForEach-Object { $_.Id } | Measure-Object -Maximum).Maximum) + 1
}

function Get-LatestBackupFile {
    if (-not (Test-Path $script:BackupDir)) { return $null }
    return Get-ChildItem -Path $script:BackupDir -Filter "inventory_*.json" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
}

function New-BackupSnapshot([string]$Content) {
    if (-not (Test-Path $script:BackupDir)) { New-Item -ItemType Directory -Path $script:BackupDir -Force | Out-Null }
    $timestamp = Get-Date -Format "yyyyMMdd_HHmmss"
    $backupPath = Join-Path $script:BackupDir "inventory_$timestamp.json"
    $suffix = 1
    while (Test-Path $backupPath) {
        $backupPath = Join-Path $script:BackupDir "inventory_${timestamp}_$suffix.json"
        $suffix++
    }
    Set-Content -Path $backupPath -Value $Content -Encoding UTF8 -NoNewline
    Prune-OldBackups
}

function Prune-OldBackups {
    if (-not (Test-Path $script:BackupDir)) { return }
    $backups = Get-ChildItem -Path $script:BackupDir -Filter "inventory_*.json" | Sort-Object LastWriteTime -Descending
    if ($backups.Count -gt $script:MaxBackups) {
        $toDelete = $backups | Select-Object -Skip $script:MaxBackups
        foreach ($b in $toDelete) { Remove-Item $b.FullName -Force -ErrorAction SilentlyContinue }
    }
}

function Invoke-PeriodicBackupCheck {
    # Runs on a timer (every $script:BackupIntervalMs), not on every edit. If nothing has
    # changed since the last snapshot, the existing one is just refreshed in place instead
    # of piling up an identical duplicate. If something changed, a new rolling snapshot is
    # taken, capped at $script:MaxBackups (oldest gets dropped once that's exceeded).
    if (-not (Test-Path $script:DataFile)) { return }
    try {
        $currentContent = Get-Content -Path $script:DataFile -Raw -Encoding UTF8
        $latest = Get-LatestBackupFile
        if ($null -eq $latest) {
            New-BackupSnapshot -Content $currentContent
            return
        }
        $latestContent = Get-Content -Path $latest.FullName -Raw -Encoding UTF8
        if ($currentContent -eq $latestContent) {
            Set-Content -Path $latest.FullName -Value $currentContent -Encoding UTF8 -NoNewline
        } else {
            New-BackupSnapshot -Content $currentContent
        }
    } catch {
        [System.Windows.Forms.MessageBox]::Show("Warning: automatic backup check failed: $($_.Exception.Message)", "Backup Warning", "OK", "Warning") | Out-Null
    }
}

function Enter-InventoryLock {
    # inventory.json is a shared file, potentially opened right now by up to ~8 other copies of
    # this app on other machines. An exclusive CreateNew open of a small separate marker file is
    # the one locking primitive that actually works across machines over a network share (unlike
    # a .NET Mutex, which is local-machine-only and would do nothing for this). Returns the open
    # FileStream (caller must pass it to Exit-InventoryLock to release), or $null on timeout.
    $deadline = (Get-Date).AddSeconds($script:LockTimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try {
            $fs = [System.IO.File]::Open($script:LockFilePath, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
            try {
                $writer = New-Object System.IO.StreamWriter($fs)
                $writer.Write("$env:USERNAME @ $env:COMPUTERNAME @ $(Get-Date -Format o)")
                $writer.Flush()
            } catch { }
            return $fs
        } catch [System.IO.IOException] {
            # Someone else holds it, or a crashed process left it behind. Only steal it if it's
            # clearly stale (far older than any real save should ever take) - never on a whim.
            try {
                if (Test-Path $script:LockFilePath) {
                    $age = (Get-Date) - (Get-Item $script:LockFilePath).LastWriteTime
                    if ($age.TotalSeconds -gt $script:LockStaleSeconds) {
                        Remove-Item -Path $script:LockFilePath -Force -ErrorAction SilentlyContinue
                        continue
                    }
                }
            } catch { }
            Start-Sleep -Milliseconds 200
        }
    }
    return $null
}

function Exit-InventoryLock($LockHandle) {
    try { if ($null -ne $LockHandle) { $LockHandle.Close() } } catch { }
    Remove-Item -Path $script:LockFilePath -Force -ErrorAction SilentlyContinue
}

function ConvertTo-InventoryRowObject($Row) {
    $obj = [ordered]@{ Id = [int]$Row.Id; Status = "$($Row.Status)"; ADFlagReason = "$($Row.ADFlagReason)"; ADComputerDisabled = "$($Row.ADComputerDisabled)"; ADComputerNotFound = "$($Row.ADComputerNotFound)"; IgnoreHighlight = "$($Row.IgnoreHighlight)" }
    foreach ($f in $script:FieldDefs) { $obj[$f.Key] = "$($Row[$f.Key])" }
    return [PSCustomObject]$obj
}

function Get-InventorySnapshotMap {
    # A cheap Id -> "all field values joined" map of $script:Items right now, used to detect
    # exactly which rows THIS session added/changed/deleted since its last load or save - never
    # by tracking edits as they happen (that would mean touching every single one of the ~20
    # places that mutate $script:Items directly), but by diffing two snapshots at save time.
    $map = @{}
    foreach ($row in $script:Items.Rows) {
        $parts = New-Object System.Collections.Generic.List[string]
        [void]$parts.Add("$($row.Status)"); [void]$parts.Add("$($row.ADFlagReason)")
        [void]$parts.Add("$($row.ADComputerDisabled)"); [void]$parts.Add("$($row.ADComputerNotFound)")
        [void]$parts.Add("$($row.IgnoreHighlight)")
        foreach ($f in $script:FieldDefs) { [void]$parts.Add("$($row[$f.Key])") }
        $map[[int]$row.Id] = ($parts -join "`u{1F}")
    }
    return $map
}

function Read-InventoryFileRaw {
    # Reads inventory.json exactly as it is on disk right now into plain objects, WITHOUT
    # touching $script:Items - used by Save-Inventory to see what other sessions have written
    # since this session's own last load, so a save can merge onto that instead of blindly
    # overwriting it.
    if (-not (Test-Path $script:DataFile)) { return @() }
    $json = Get-Content -Path $script:DataFile -Raw -Encoding UTF8
    if ([string]::IsNullOrWhiteSpace($json)) { return @() }
    $parsed = $json | ConvertFrom-Json
    # ConvertFrom-Json already returns a flat array for a multi-element JSON array (confirmed by
    # reproduction) - wrapping THAT in @() nests it into a 1-element array-of-one-array instead
    # of flattening it. Only a single JSON object (the one-row case) actually needs @() to
    # become iterable/countable like the multi-row case.
    if ($null -eq $parsed) { return @() }
    if ($parsed -is [array]) { return $parsed }
    return @($parsed)
}

function Save-Inventory {
    # Multi-user-safe save: merges only THIS session's own added/changed/deleted rows (found by
    # diffing $script:Items against the snapshot taken at its last load/save) onto whatever is
    # CURRENTLY on disk, under a cross-machine lock - never a blind full-table overwrite. That
    # means two people saving different rows around the same time both keep their changes,
    # instead of whoever saves last silently erasing the other's edit.
    $lock = Enter-InventoryLock
    if ($null -eq $lock) {
        [System.Windows.Forms.MessageBox]::Show("Could not save - the inventory file has been locked by another user for too long. Your changes are still here; try Save again in a moment.", "Save Locked", "OK", "Warning") | Out-Null
        return
    }
    try {
        $localSnapshot = Get-InventorySnapshotMap
        $priorSnapshot = if ($null -ne $script:LastSyncSnapshot) { $script:LastSyncSnapshot } else { @{} }

        $changedIds = New-Object System.Collections.Generic.List[int]
        foreach ($id in $localSnapshot.Keys) {
            if (-not $priorSnapshot.ContainsKey($id) -or $priorSnapshot[$id] -ne $localSnapshot[$id]) { $changedIds.Add($id) }
        }
        $deletedIds = New-Object System.Collections.Generic.List[int]
        foreach ($id in $priorSnapshot.Keys) {
            if (-not $localSnapshot.ContainsKey($id)) { $deletedIds.Add($id) }
        }

        # Plain Hashtable, NOT [ordered]@{} - an OrderedDictionary's integer indexer is
        # positional (like an array index), not key-based, so $diskMap[2] with only 1 entry
        # throws "index out of range" instead of doing a key lookup. Confirmed by reproduction.
        # Order is restored with an explicit sort below instead.
        $diskMap = @{}
        foreach ($r in (Read-InventoryFileRaw)) { $diskMap[[int]$r.Id] = $r }

        foreach ($id in $changedIds) {
            $localRow = $script:Items.Rows.Find($id)
            # A row this session both added AND then deleted again before ever saving won't be
            # found - nothing to merge in, which is correct.
            if ($null -ne $localRow) { $diskMap[$id] = ConvertTo-InventoryRowObject $localRow }
        }
        foreach ($id in $deletedIds) {
            # If someone else's concurrent edit to this same row is sitting on disk and this
            # session deleted it locally, the deletion wins - safer than silently resurrecting a
            # row someone intentionally removed (e.g. mid-offboarding cleanup).
            if ($diskMap.ContainsKey($id)) { $diskMap.Remove($id) }
        }

        $merged = @($diskMap.Values | Sort-Object Id)
        $tempPath = "$($script:DataFile).tmp"
        $merged | ConvertTo-Json -Depth 3 | Set-Content -Path $tempPath -Encoding UTF8
        # Write-then-rename, not a direct write to $script:DataFile - a reader (another session's
        # Load-Inventory, or this one's own periodic refresh) can never see a half-written file.
        # Move-Item -Force handles both "destination exists" and "doesn't yet" the same way -
        # confirmed [System.IO.File]::Replace() fails with "path is not of a legal form" in this
        # environment even on plainly valid paths, so this is deliberately not used here.
        Move-Item -Path $tempPath -Destination $script:DataFile -Force
    } finally {
        Exit-InventoryLock $lock
    }

    # Reload from the merged, now-canonical file - this also picks up anything OTHER sessions
    # added/changed since this session's own last load, not just what this save just wrote.
    Load-Inventory
}

function Load-Inventory {
    $script:Items.Rows.Clear()
    if (Test-Path $script:DataFile) {
        try {
            $json = Get-Content -Path $script:DataFile -Raw -Encoding UTF8
            if (-not [string]::IsNullOrWhiteSpace($json)) {
                $data = $json | ConvertFrom-Json
                foreach ($item in $data) {
                    $newRow = $script:Items.NewRow()
                    $newRow["Id"] = [int]$item.Id
                    $status = $item.Status
                    if ([string]::IsNullOrWhiteSpace("$status")) { $status = "Current" }
                    $newRow["Status"] = $status
                    $adFlag = $item.ADFlagReason
                    if ($null -eq $adFlag) { $adFlag = "" }
                    $newRow["ADFlagReason"] = "$adFlag"
                    $adCompDisabled = $item.ADComputerDisabled
                    if ($null -eq $adCompDisabled) { $adCompDisabled = "" }
                    $newRow["ADComputerDisabled"] = "$adCompDisabled"
                    $adCompNotFound = $item.ADComputerNotFound
                    if ($null -eq $adCompNotFound) { $adCompNotFound = "" }
                    $newRow["ADComputerNotFound"] = "$adCompNotFound"
                    $ignoreHighlight = $item.IgnoreHighlight
                    if ($null -eq $ignoreHighlight) { $ignoreHighlight = "" }
                    $newRow["IgnoreHighlight"] = "$ignoreHighlight"
                    foreach ($f in $script:FieldDefs) {
                        $val = $item.($f.Key)
                        if ($null -eq $val) { $val = "" }
                        $newRow[$f.Key] = [string]$val
                    }
                    $script:Items.Rows.Add($newRow)
                }
            }
        } catch {
            [System.Windows.Forms.MessageBox]::Show("Could not load inventory.json: $($_.Exception.Message)", "Load Error", "OK", "Warning") | Out-Null
        }
    }
    # Baseline for the next Save-Inventory's diff, and for the periodic cross-session refresh -
    # every code path that changes what's on disk goes through here (Save-Inventory reloads at
    # the end, Restore Backup calls this directly too), so this is the one place both need to
    # stay current.
    $script:LastSyncSnapshot = Get-InventorySnapshotMap
    $script:LastKnownFileWriteTimeUtc = if (Test-Path $script:DataFile) { (Get-Item $script:DataFile).LastWriteTimeUtc } else { $null }
}

function Show-ItemDialog {
    param(
        [string]$Title = "Add Item",
        [hashtable]$Values = $null
    )

    $dlg = New-Object System.Windows.Forms.Form
    $dlg.Text = $Title
    $dlg.Size = New-Object System.Drawing.Size(680, 420)
    $dlg.StartPosition = "CenterParent"
    $dlg.FormBorderStyle = "FixedDialog"
    $dlg.MaximizeBox = $false
    $dlg.MinimizeBox = $false

    $controls = @{}
    $count = $script:VisibleFieldDefs.Count
    $half = [Math]::Ceiling($count / 2.0)
    $rowHeight = 35
    $startY = 15

    for ($i = 0; $i -lt $count; $i++) {
        $f = $script:VisibleFieldDefs[$i]
        if ($i -lt $half) {
            $labelX = 15; $labelW = 115; $ctrlX = 135; $ctrlW = 160
            $row = $i
        } else {
            $labelX = 330; $labelW = 115; $ctrlX = 450; $ctrlW = 160
            $row = $i - $half
        }
        $y = $startY + ($row * $rowHeight)

        $lbl = New-Object System.Windows.Forms.Label
        $lbl.Text = "$($f.Label):"
        $lbl.Location = New-Object System.Drawing.Point($labelX, $y)
        $lbl.Size = New-Object System.Drawing.Size($labelW, 20)
        $dlg.Controls.Add($lbl)

        $currentValue = ""
        if ($null -ne $Values -and $Values.ContainsKey($f.Key)) {
            $currentValue = $Values[$f.Key]
        } else {
            $currentValue = Get-FieldDefault $f
        }

        if ($f.Type -eq "dropdown") {
            $cmb = New-Object System.Windows.Forms.ComboBox
            $cmb.Location = New-Object System.Drawing.Point($ctrlX, ($y - 3))
            $cmb.Size = New-Object System.Drawing.Size($ctrlW, 20)
            $cmb.DropDownStyle = "DropDownList"
            [void]$cmb.Items.AddRange($f.Options)
            if ($cmb.Items.Contains($currentValue)) {
                $cmb.SelectedItem = $currentValue
            } else {
                $cmb.SelectedItem = $f.Default
            }
            $dlg.Controls.Add($cmb)
            $controls[$f.Key] = $cmb
        } elseif ($f.Type -eq "dynamicdropdown") {
            # Editable combo (DropDown, not DropDownList) - pick a value already used elsewhere
            # in the inventory, or just type a new one if this is genuinely a new model. Reading
            # it back uses the same .Text.Trim() path as a plain textbox (see the OK handler and
            # every other place that already only special-cases the static "dropdown" type), so
            # nothing downstream needed to change for that half.
            $cmb = New-Object System.Windows.Forms.ComboBox
            $cmb.Location = New-Object System.Drawing.Point($ctrlX, ($y - 3))
            $cmb.Size = New-Object System.Drawing.Size($ctrlW, 20)
            $cmb.DropDownStyle = "DropDown"
            [void]$cmb.Items.AddRange((Get-DistinctFieldValues $f.Key))
            $cmb.Text = $currentValue
            $dlg.Controls.Add($cmb)
            $controls[$f.Key] = $cmb
        } else {
            $txt = New-Object System.Windows.Forms.TextBox
            $txt.Location = New-Object System.Drawing.Point($ctrlX, ($y - 3))
            $txt.Size = New-Object System.Drawing.Size($ctrlW, 20)
            $txt.Text = $currentValue
            if ($f.ContainsKey("ReadOnly") -and $f.ReadOnly) {
                $txt.ReadOnly = $true
                $txt.TabStop = $false
                $txt.BackColor = [System.Drawing.SystemColors]::Control
            }
            $dlg.Controls.Add($txt)
            $controls[$f.Key] = $txt
        }
    }

    $btnY = $startY + ($half * $rowHeight) + 15

    $btnOK = New-Object System.Windows.Forms.Button
    $btnOK.Text = "OK"
    $btnOK.Location = New-Object System.Drawing.Point(450, $btnY)
    $btnOK.DialogResult = [System.Windows.Forms.DialogResult]::OK
    $dlg.Controls.Add($btnOK)

    $btnCancel = New-Object System.Windows.Forms.Button
    $btnCancel.Text = "Cancel"
    $btnCancel.Location = New-Object System.Drawing.Point(535, $btnY)
    $btnCancel.DialogResult = [System.Windows.Forms.DialogResult]::Cancel
    $dlg.Controls.Add($btnCancel)

    $dlg.AcceptButton = $btnOK
    $dlg.CancelButton = $btnCancel
    $dlg.ClientSize = New-Object System.Drawing.Size(660, ($btnY + 45))

    $result = $dlg.ShowDialog()

    if ($result -eq [System.Windows.Forms.DialogResult]::OK) {
        $deviceNameCtrl = $controls["DeviceName"]
        if ([string]::IsNullOrWhiteSpace($deviceNameCtrl.Text)) {
            [System.Windows.Forms.MessageBox]::Show("Device Name is required.", "Validation Error", "OK", "Warning") | Out-Null
            return $null
        }
        $out = @{}
        foreach ($f in $script:VisibleFieldDefs) {
            $ctrl = $controls[$f.Key]
            if ($f.Type -eq "dropdown") {
                $out[$f.Key] = "$($ctrl.SelectedItem)"
            } else {
                $out[$f.Key] = $ctrl.Text.Trim()
            }
        }
        return $out
    }
    return $null
}

function Get-SanitizedDropdownValue([hashtable]$field, [string]$value) {
    if ([string]::IsNullOrWhiteSpace($value)) { return $field.Default }
    foreach ($opt in $field.Options) {
        if ($opt -eq $value) { return $opt }
    }
    return $field.Default
}

function Build-CsvHeaderMap($sampleRow) {
    $map = @{}
    foreach ($prop in $sampleRow.PSObject.Properties) {
        $map[$prop.Name.Trim().ToLowerInvariant()] = $prop.Name
    }
    return $map
}

function Get-CsvFieldValue($row, [hashtable]$field, [hashtable]$headerMap) {
    $candidates = @($field.Key, $field.Label)
    if ($field.ContainsKey("Aliases")) { $candidates += $field.Aliases }
    foreach ($candidate in $candidates) {
        $normalized = $candidate.Trim().ToLowerInvariant()
        if ($headerMap.ContainsKey($normalized)) {
            return $row.($headerMap[$normalized])
        }
    }
    return $null
}

# ---- Per-tab control registries, keyed by Status key ----
$script:Grids = @{}
$script:Views = @{}
$script:SearchBoxes = @{}
$script:StatusLabels = @{}
$script:SerialColIndex = @{}
$script:ADFlagColIndex = @{}
$script:ADComputerColIndex = @{}
$script:ADComputerNotFoundColIndex = @{}
$script:IgnoreHighlightColIndex = @{}
$script:DeviceNameColIndex = @{}
$script:GroupPrimaryColIndex = @{}
$script:GroupCountColIndex = @{}
$script:StatusColIndex = @{}
$script:ContextMenuValidRow = @{}
# Base filter per tab key - normally "[Status] = 'X'" for the 5 real statuses, but a filtered
# view like "Off Domain" (grouped by the Domain field, independent of Status) registers a
# custom clause here instead. Every function that scopes work "to this tab" reads from here
# rather than assuming Status-based filtering.
$script:BaseFilters = @{}

# Devices whose duplicate group the user has manually expanded (DeviceName, trimmed/lowercased ->
# $true). Purely in-memory UI state - resets on relaunch, never saved to inventory.json.
$script:ExpandedGroups = @{}

# Recomputes the GroupCollapsed/GroupPrimary/GroupCount columns from the current DeviceName
# values. Devices used by more than one person end up as several rows sharing the same
# DeviceName; the lowest-Id row becomes the "primary" (always visible, shown with an
# expand/collapse arrow), and the rest are hidden (GroupCollapsed = "Yes") unless the user has
# expanded that device via Toggle-DeviceGroup. Must run before any RowFilter that references
# GroupCollapsed is (re)applied.
function Update-DuplicateGroups {
    foreach ($row in $script:Items.Rows) {
        $row["GroupCollapsed"] = ""
        $row["GroupPrimary"] = ""
        $row["GroupCount"] = ""
    }
    # Grouped by Status + DeviceName (not DeviceName alone) so a device that's inconsistently
    # recorded in two different tabs (e.g. still "Current" here but also "Terminated" there)
    # stays visible in both instead of silently collapsing away a data-quality problem the
    # app's other highlight features are specifically designed to surface.
    $groups = $script:Items.Rows |
        Where-Object { -not [string]::IsNullOrWhiteSpace("$($_.DeviceName)") } |
        Group-Object { "$($_.Status)".Trim().ToLowerInvariant() + "|" + "$($_.DeviceName)".Trim().ToLowerInvariant() }
    foreach ($g in $groups) {
        if ($g.Count -le 1) { continue }
        $sorted = @($g.Group | Sort-Object Id)
        $sorted[0]["GroupPrimary"] = "Yes"
        foreach ($member in $sorted) { $member["GroupCount"] = "$($g.Count)" }
        $expanded = $script:ExpandedGroups.ContainsKey($g.Name) -and $script:ExpandedGroups[$g.Name]
        if (-not $expanded) {
            for ($i = 1; $i -lt $sorted.Count; $i++) { $sorted[$i]["GroupCollapsed"] = "Yes" }
        }
    }
}

function Toggle-DeviceGroup([string]$DeviceKey) {
    if ([string]::IsNullOrWhiteSpace($DeviceKey)) { return }
    $current = $script:ExpandedGroups.ContainsKey($DeviceKey) -and $script:ExpandedGroups[$DeviceKey]
    $script:ExpandedGroups[$DeviceKey] = -not $current
    Refresh-AllTabs
}

function Apply-Filter([string]$StatusKey) {
    $view = $script:Views[$StatusKey]
    $txtSearch = $script:SearchBoxes[$StatusKey]
    $term = $txtSearch.Text.Trim().Replace("'", "''")
    $clauses = @($script:BaseFilters[$StatusKey])
    if ([string]::IsNullOrWhiteSpace($term)) {
        # Collapsed duplicate-group members stay hidden until expanded - but a search should
        # always be able to surface them, since "why can't I find John's laptop" would be a
        # confusing result for a feature meant to reduce clutter, not hide data.
        $clauses += "[GroupCollapsed] <> 'Yes'"
    } else {
        $fieldClauses = $script:VisibleFieldDefs | ForEach-Object { "[$($_.Key)] LIKE '%$term%'" }
        $clauses += "(" + ($fieldClauses -join " OR ") + ")"
    }
    $view.RowFilter = $clauses -join " AND "
}

function Update-TabStatusLabel([string]$StatusKey) {
    $lbl = $script:StatusLabels[$StatusKey]
    $rows = $script:Items.Select($script:BaseFilters[$StatusKey])
    $total = $rows.Count
    $missing = @($rows | Where-Object { [string]::IsNullOrWhiteSpace($_["SerialNumber"]) }).Count
    $adDisabled = @($rows | Where-Object { "$($_["ADFlagReason"])" -eq "AD account disabled" }).Count
    $userNotFound = @($rows | Where-Object { "$($_["ADFlagReason"])" -eq "No matching AD user found" }).Count
    $compNotFound = @($rows | Where-Object { -not [string]::IsNullOrWhiteSpace("$($_["ADComputerNotFound"])") }).Count
    $compDisabled = @($rows | Where-Object { -not [string]::IsNullOrWhiteSpace("$($_["ADComputerDisabled"])") }).Count
    $lbl.Text = "$total total, $missing no Serial #, $adDisabled AD-disabled, $userNotFound user not in AD, $compNotFound computer not in AD, $compDisabled computer disabled"
}

function Refresh-AllTabs {
    Update-DuplicateGroups
    foreach ($key in $script:Grids.Keys) {
        $script:Grids[$key].Refresh()
        Update-TabStatusLabel $key
    }
}

# Persistent activity log shown in the side panel - set once the main form builds it.
$script:LogListBox = $null

function Write-AppLog([string]$Message, [string]$Level = "Info") {
    $timestamp = Get-Date -Format "HH:mm:ss"
    $prefix = switch ($Level) { "Error" { "ERROR" } "Warning" { "WARN" } default { "INFO" } }
    $line = "[$timestamp] ${prefix}: $Message"

    if ($null -ne $script:LogListBox) {
        [void]$script:LogListBox.Items.Add($line)
        $script:LogListBox.SelectedIndex = $script:LogListBox.Items.Count - 1
        $script:LogListBox.ClearSelected()
    }

    try {
        if (-not (Test-Path $script:LogDir)) { New-Item -Path $script:LogDir -ItemType Directory -Force | Out-Null }
        # Add-Content has a known Windows PowerShell 5.1 quirk where its internal file-content
        # provider can leave a cached handle open across calls in a long-running process,
        # making later writes fail with "being used by another process" (confirmed by
        # reproduction - two real onboard/offboard runs in the same session both silently lost
        # their log lines this way). Opening/writing/closing via .NET directly, with explicit
        # ReadWrite sharing, avoids that caching entirely - every call is self-contained.
        $fileLine = "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') $prefix $Message`r`n"
        $stream = [System.IO.File]::Open($script:LogFilePath, [System.IO.FileMode]::Append, [System.IO.FileAccess]::Write, [System.IO.FileShare]::ReadWrite)
        try {
            $writer = New-Object System.IO.StreamWriter($stream, [System.Text.Encoding]::UTF8)
            $writer.Write($fileLine)
            $writer.Flush()
        } finally {
            $writer.Dispose()
            $stream.Dispose()
        }
    } catch {
        # Logging failures must never crash the app - the in-app list above already has the
        # message even if the file write failed.
    }
}

function Add-Item([string]$StatusKey) {
    $result = Show-ItemDialog -Title "Add Item"
    if ($null -ne $result) {
        $newRow = $script:Items.NewRow()
        $newRow["Id"] = Get-NextId
        $newRow["Status"] = $StatusKey
        foreach ($f in $script:FieldDefs) { $newRow[$f.Key] = $result[$f.Key] }
        $script:Items.Rows.Add($newRow)
        Save-Inventory
        Refresh-AllTabs
        Sync-AdFieldsAfterSave -DeviceName $result["DeviceName"] -OldValues $null -NewValues $result
    }
}

function Edit-Item([string]$StatusKey) {
    $grid = $script:Grids[$StatusKey]
    if ($grid.SelectedRows.Count -eq 0) {
        [System.Windows.Forms.MessageBox]::Show("Select an item to edit.", "No Selection", "OK", "Information") | Out-Null
        return
    }
    # Capture Ids up front rather than re-reading grid.SelectedRows inside the loop - each
    # save below can shift/filter the grid's live rows (e.g. if a search filter no longer
    # matches after the edit), so Id-based lookups via $script:Items.Rows.Find are the only
    # reliable way to keep walking the originally-selected set across multiple saves.
    # SelectedRows does NOT preserve visual row order (confirmed - it can come back reversed),
    # so sort by Index to walk them top-to-bottom, matching what "(1 of N)" implies on screen.
    $ids = @($grid.SelectedRows | Sort-Object Index | ForEach-Object { $_.DataBoundItem["Id"] })
    $total = $ids.Count
    $index = 0
    foreach ($id in $ids) {
        $index++
        $dataRow = $script:Items.Rows.Find($id)
        if ($null -eq $dataRow) { continue }
        $currentValues = @{}
        foreach ($f in $script:FieldDefs) { $currentValues[$f.Key] = "$($dataRow[$f.Key])" }
        $title = if ($total -gt 1) { "Edit Item ($index of $total) - $($dataRow["DeviceName"])" } else { "Edit Item" }
        $result = Show-ItemDialog -Title $title -Values $currentValues
        if ($null -ne $result) {
            # Hidden fields (e.g. FormerUser) never got a control in the dialog, so $result has
            # no entry for them - only write back the fields that were actually editable, or
            # this would blank out hidden data on every single edit.
            foreach ($f in $script:VisibleFieldDefs) { $dataRow[$f.Key] = $result[$f.Key] }
            Save-Inventory
            Refresh-AllTabs
            Sync-AdFieldsAfterSave -DeviceName $result["DeviceName"] -OldValues $currentValues -NewValues $result
        }
    }
}

function Delete-Items([string]$StatusKey) {
    $grid = $script:Grids[$StatusKey]
    if ($grid.SelectedRows.Count -eq 0) {
        [System.Windows.Forms.MessageBox]::Show("Select an item to delete.", "No Selection", "OK", "Information") | Out-Null
        return
    }
    $ids = @($grid.SelectedRows | Sort-Object Index | ForEach-Object { $_.DataBoundItem["Id"] })
    if ($ids.Count -eq 1) {
        $name = ($grid.SelectedRows[0].DataBoundItem["DeviceName"])
        $promptText = "Delete '$name'?"
    } else {
        $promptText = "Delete $($ids.Count) selected items?"
    }
    $confirm = [System.Windows.Forms.MessageBox]::Show($promptText, "Confirm Delete", "YesNo", "Warning")
    if ($confirm -eq [System.Windows.Forms.DialogResult]::Yes) {
        foreach ($id in $ids) {
            $dataRow = $script:Items.Rows.Find($id)
            if ($null -ne $dataRow) { $script:Items.Rows.Remove($dataRow) }
        }
        Save-Inventory
        Refresh-AllTabs
    }
}

function Get-StatusKeyForLabel([string]$Label) {
    # Deliberately a real function, not inlined at the call site: a raw {} scriptblock passed
    # to .add_Click() and only made to work via .GetNewClosure() can correctly READ local
    # variables it captured, but a direct "$script:X" reference written INSIDE that closure's
    # own body (read or write) resolves against GetNewClosure()'s own isolated snapshot scope,
    # not the real running script - it silently sees an empty/default value instead of erroring.
    # Wrapping the $script:Statuses lookup in an ordinary function sidesteps this entirely,
    # since a normal function call resolves $script: against the real script's scope regardless
    # of which closure called it. (Confirmed by reproduction - this exact shape of bug is what
    # made the Move button silently do nothing useful.)
    return ($script:Statuses | Where-Object { $_.Label -eq $Label }).Key
}

function Move-SelectedItems([string]$FromStatus, [string]$ToStatus) {
    $grid = $script:Grids[$FromStatus]
    if ($grid.SelectedRows.Count -eq 0) {
        [System.Windows.Forms.MessageBox]::Show("Select an item to move.", "No Selection", "OK", "Information") | Out-Null
        return
    }
    $ids = @($grid.SelectedRows | Sort-Object Index | ForEach-Object { $_.DataBoundItem["Id"] })
    $toLabel = ($script:Statuses | Where-Object { $_.Key -eq $ToStatus }).Label
    if ($ids.Count -eq 1) {
        $name = ($grid.SelectedRows[0].DataBoundItem["DeviceName"])
        # ${toLabel} (brace syntax), not bare $toLabel - PowerShell parses a bare "$toLabel?" in
        # a double-quoted string as a reference to a variable literally NAMED "toLabel?" (which
        # doesn't exist, so it silently interpolates to ""), not as $toLabel followed by a
        # literal "?". This is what made every "Move to X?" confirmation show "Move to " with no
        # destination at all - confirmed by reproduction outside the app entirely.
        $promptText = "Move '$name' to ${toLabel}?"
    } else {
        $promptText = "Move $($ids.Count) selected items to ${toLabel}?"
    }
    $confirm = [System.Windows.Forms.MessageBox]::Show($promptText, "Confirm Move", "YesNo", "Question")
    if ($confirm -eq [System.Windows.Forms.DialogResult]::Yes) {
        foreach ($id in $ids) {
            $dataRow = $script:Items.Rows.Find($id)
            if ($null -ne $dataRow) {
                $dataRow["Status"] = $ToStatus
                if ($ToStatus -eq "Terminated") { $dataRow["ADFlagReason"] = "" }
                if ($FromStatus -eq "DisabledDevices" -and $ToStatus -ne "DisabledDevices") { $dataRow["ADComputerDisabled"] = "" }
                if ($FromStatus -eq "NotInAD" -and $ToStatus -ne "NotInAD") { $dataRow["ADFlagReason"] = "" }
                if ($FromStatus -eq "ComputerNotInAD" -and $ToStatus -ne "ComputerNotInAD") { $dataRow["ADComputerNotFound"] = "" }
            }
        }
        Save-Inventory
        Refresh-AllTabs
    }
}

function Set-DeviceDomain([string]$StatusKey, [string]$DomainValue, [string]$DomainLabel) {
    $grid = $script:Grids[$StatusKey]
    if ($grid.SelectedRows.Count -eq 0) {
        [System.Windows.Forms.MessageBox]::Show("Select an item to move.", "No Selection", "OK", "Information") | Out-Null
        return
    }
    $ids = @($grid.SelectedRows | Sort-Object Index | ForEach-Object { $_.DataBoundItem["Id"] })
    if ($ids.Count -eq 1) {
        $name = ($grid.SelectedRows[0].DataBoundItem["DeviceName"])
        # ${DomainLabel} (brace syntax) - see the matching note in Move-SelectedItems; a bare
        # "$DomainLabel?" silently interpolates to "" instead of the real value.
        $promptText = "Move '$name' to ${DomainLabel}?"
    } else {
        $promptText = "Move $($ids.Count) selected items to ${DomainLabel}?"
    }
    $confirm = [System.Windows.Forms.MessageBox]::Show($promptText, "Confirm Move", "YesNo", "Question")
    if ($confirm -eq [System.Windows.Forms.DialogResult]::Yes) {
        foreach ($id in $ids) {
            $dataRow = $script:Items.Rows.Find($id)
            if ($null -ne $dataRow) { $dataRow["Domain"] = $DomainValue }
        }
        Save-Inventory
        Refresh-AllTabs
    }
}

function Set-IgnoreHighlight([string]$StatusKey, [bool]$Ignore) {
    $grid = $script:Grids[$StatusKey]
    if ($grid.SelectedRows.Count -eq 0) {
        [System.Windows.Forms.MessageBox]::Show("Select one or more items first.", "No Selection", "OK", "Information") | Out-Null
        return
    }
    $ids = @($grid.SelectedRows | Sort-Object Index | ForEach-Object { $_.DataBoundItem["Id"] })
    foreach ($id in $ids) {
        $dataRow = $script:Items.Rows.Find($id)
        if ($null -ne $dataRow) { $dataRow["IgnoreHighlight"] = if ($Ignore) { "Yes" } else { "" } }
    }
    Save-Inventory
    Refresh-AllTabs
}

function Invoke-ToggleHighlight([string]$StatusKey) {
    # One menu item instead of two opposite ones - flips ALL selected rows to the opposite of
    # whatever the FIRST selected row currently has, matching the label the context menu already
    # computed for this same selection (see the menu's add_Opening handler).
    $grid = $script:Grids[$StatusKey]
    if ($grid.SelectedRows.Count -eq 0) {
        [System.Windows.Forms.MessageBox]::Show("Select one or more items first.", "No Selection", "OK", "Information") | Out-Null
        return
    }
    $firstRow = $script:Items.Rows.Find($grid.SelectedRows[0].DataBoundItem["Id"])
    $currentlyIgnored = ($null -ne $firstRow) -and ("$($firstRow.IgnoreHighlight)" -eq "Yes")
    Set-IgnoreHighlight -StatusKey $StatusKey -Ignore (-not $currentlyIgnored)
}

function Clear-Username([string]$StatusKey) {
    # Once a device is anchored to a Username, Match User trusts it completely and just
    # refreshes First/Last Name from that same AD account - it never re-searches by name. If
    # the anchor is wrong (device reassigned to someone else, bad match the first time), this
    # is the only way to reset it back to a fresh Last-Name search on the next Match User.
    $grid = $script:Grids[$StatusKey]
    if ($grid.SelectedRows.Count -eq 0) {
        [System.Windows.Forms.MessageBox]::Show("Select one or more items first.", "No Selection", "OK", "Information") | Out-Null
        return
    }
    $ids = @($grid.SelectedRows | Sort-Object Index | ForEach-Object { $_.DataBoundItem["Id"] })
    $promptText = if ($ids.Count -eq 1) {
        "Clear the matched Username for '$($grid.SelectedRows[0].DataBoundItem["DeviceName"])'? The next Match User will search AD by Last Name instead of reusing it."
    } else {
        "Clear the matched Username for $($ids.Count) selected item(s)? The next Match User will search AD by Last Name instead of reusing it."
    }
    $confirm = [System.Windows.Forms.MessageBox]::Show($promptText, "Confirm Clear Username", "YesNo", "Question")
    if ($confirm -ne [System.Windows.Forms.DialogResult]::Yes) { return }
    foreach ($id in $ids) {
        $dataRow = $script:Items.Rows.Find($id)
        if ($null -ne $dataRow) { $dataRow["Username"] = "" }
    }
    Save-Inventory
    Refresh-AllTabs
    Write-AppLog "Cleared Username anchor for $($ids.Count) item(s) - next Match User will search AD by Last Name." "Info"
}

function Open-BackupsFolder {
    if (-not (Test-Path $script:BackupDir)) { New-Item -ItemType Directory -Path $script:BackupDir -Force | Out-Null }
    Start-Process explorer.exe $script:BackupDir
}

function Show-RestoreBackupDialog {
    if (-not (Test-Path $script:BackupDir)) {
        [System.Windows.Forms.MessageBox]::Show("No backups exist yet - the first automatic backup is taken within 5 minutes of opening the app or making a change.", "Restore from Backup", "OK", "Information") | Out-Null
        return
    }
    $backups = Get-ChildItem -Path $script:BackupDir -Filter "inventory_*.json" | Sort-Object LastWriteTime -Descending
    if ($backups.Count -eq 0) {
        [System.Windows.Forms.MessageBox]::Show("No backups exist yet - the first automatic backup is taken within 5 minutes of opening the app or making a change.", "Restore from Backup", "OK", "Information") | Out-Null
        return
    }

    $dlg = New-Object System.Windows.Forms.Form
    $dlg.Text = "Restore from Backup"
    $dlg.Size = New-Object System.Drawing.Size(440, 440)
    $dlg.StartPosition = "CenterParent"
    $dlg.FormBorderStyle = "FixedDialog"
    $dlg.MaximizeBox = $false
    $dlg.MinimizeBox = $false

    $lbl = New-Object System.Windows.Forms.Label
    $lbl.Text = "A backup is checked every 5 minutes and only updated if something changed (last $script:MaxBackups kept). Pick a point in time to restore to - your current data is backed up first, so this itself can't lose anything."
    $lbl.Location = New-Object System.Drawing.Point(10, 10)
    $lbl.Size = New-Object System.Drawing.Size(410, 65)
    $dlg.Controls.Add($lbl)

    $listBox = New-Object System.Windows.Forms.ListBox
    $listBox.Location = New-Object System.Drawing.Point(10, 80)
    $listBox.Size = New-Object System.Drawing.Size(410, 280)
    $listBox.Font = New-Object System.Drawing.Font("Consolas", 9)
    foreach ($b in $backups) {
        [void]$listBox.Items.Add($b.LastWriteTime.ToString("yyyy-MM-dd  HH:mm:ss"))
    }
    $listBox.SelectedIndex = 0
    $dlg.Controls.Add($listBox)

    $btnRestore = New-Object System.Windows.Forms.Button
    $btnRestore.Text = "Restore"
    $btnRestore.Location = New-Object System.Drawing.Point(230, 370)
    $btnRestore.DialogResult = [System.Windows.Forms.DialogResult]::OK
    $dlg.Controls.Add($btnRestore)

    $btnCancel = New-Object System.Windows.Forms.Button
    $btnCancel.Text = "Cancel"
    $btnCancel.Location = New-Object System.Drawing.Point(320, 370)
    $btnCancel.DialogResult = [System.Windows.Forms.DialogResult]::Cancel
    $dlg.Controls.Add($btnCancel)

    $dlg.AcceptButton = $btnRestore
    $dlg.CancelButton = $btnCancel

    if ($dlg.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK -and $listBox.SelectedIndex -ge 0) {
        $selectedBackup = $backups[$listBox.SelectedIndex]
        $confirm = [System.Windows.Forms.MessageBox]::Show(
            "Replace the current inventory with the backup from $($selectedBackup.LastWriteTime)?`n`nYour current data will be backed up first, so you can always undo this.",
            "Confirm Restore", "YesNo", "Warning")
        if ($confirm -eq [System.Windows.Forms.DialogResult]::Yes) {
            # Read the chosen backup's content into memory BEFORE snapshotting/pruning runs,
            # so a prune triggered by the pre-restore backup can never delete the very file
            # we're about to restore out from under us.
            $restoreContent = Get-Content -Path $selectedBackup.FullName -Raw -Encoding UTF8
            Invoke-PeriodicBackupCheck
            # A restore is a real, intentional full-file replacement (unlike Save-Inventory's
            # per-row merge) - it still needs the same cross-machine lock, though, so it can't
            # land in the middle of some other session's own save.
            $restoreLock = Enter-InventoryLock
            if ($null -eq $restoreLock) {
                [System.Windows.Forms.MessageBox]::Show("Could not restore - the inventory file has been locked by another user for too long. Try again in a moment.", "Restore Locked", "OK", "Warning") | Out-Null
                return
            }
            try {
                Set-Content -Path $script:DataFile -Value $restoreContent -Encoding UTF8 -NoNewline
            } finally {
                Exit-InventoryLock $restoreLock
            }
            Load-Inventory
            $script:Items.PrimaryKey = @($script:Items.Columns["Id"])
            foreach ($s in $script:Statuses) {
                Apply-Filter -StatusKey $s.Key
                Update-TabStatusLabel -StatusKey $s.Key
            }
            Apply-Filter -StatusKey "OffDomain"
            Update-TabStatusLabel -StatusKey "OffDomain"
            [System.Windows.Forms.MessageBox]::Show("Inventory restored.", "Restore Complete", "OK", "Information") | Out-Null
        }
    }
    $dlg.Dispose()
}

function Import-ItemsCsv([string]$StatusKey) {
    $ofd = New-Object System.Windows.Forms.OpenFileDialog
    $ofd.Filter = "CSV Files (*.csv)|*.csv|All Files (*.*)|*.*"
    if ($ofd.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {
        try {
            $rows = Import-Csv -Path $ofd.FileName
            $added = 0
            $skipped = 0
            if ($rows.Count -gt 0) {
                $headerMap = Build-CsvHeaderMap $rows[0]
                $deviceNameField = $script:FieldDefs | Where-Object { $_.Key -eq "DeviceName" }
                $lastNameField = $script:FieldDefs | Where-Object { $_.Key -eq "LastName" }
                $firstNameField = $script:FieldDefs | Where-Object { $_.Key -eq "FirstName" }
                foreach ($r in $rows) {
                    $deviceNameVal = Get-CsvFieldValue $r $deviceNameField $headerMap
                    if ([string]::IsNullOrWhiteSpace($deviceNameVal)) {
                        $lastNameVal = Get-CsvFieldValue $r $lastNameField $headerMap
                        $firstNameVal = Get-CsvFieldValue $r $firstNameField $headerMap
                        if ([string]::IsNullOrWhiteSpace($lastNameVal) -and [string]::IsNullOrWhiteSpace($firstNameVal)) {
                            $skipped++
                            continue
                        }
                        $deviceNameVal = "No PC"
                    }
                    $newRow = $script:Items.NewRow()
                    $newRow["Id"] = Get-NextId
                    $newRow["Status"] = $StatusKey
                    foreach ($f in $script:FieldDefs) {
                        if ($f.Key -eq "DeviceName") {
                            $newRow[$f.Key] = $deviceNameVal.Trim()
                            continue
                        }
                        $val = Get-CsvFieldValue $r $f $headerMap
                        if ($f.Type -eq "dropdown") {
                            $newRow[$f.Key] = Get-SanitizedDropdownValue $f $val
                        } else {
                            if ($null -eq $val) { $val = "" }
                            $newRow[$f.Key] = $val.Trim()
                        }
                    }
                    $script:Items.Rows.Add($newRow)
                    $added++
                }
            }
            Save-Inventory
            Refresh-AllTabs
            $msg = "Imported $added item(s)."
            if ($skipped -gt 0) { $msg += "`nSkipped $skipped completely empty row(s)." }
            [System.Windows.Forms.MessageBox]::Show($msg, "Import Complete", "OK", "Information") | Out-Null
        } catch {
            [System.Windows.Forms.MessageBox]::Show("Import failed: $($_.Exception.Message)", "Import Error", "OK", "Error") | Out-Null
        }
    }
}

function Export-ItemsCsv([string]$StatusKey) {
    $sfd = New-Object System.Windows.Forms.SaveFileDialog
    $sfd.Filter = "CSV Files (*.csv)|*.csv"
    $sfd.FileName = "inventory_export.csv"
    if ($sfd.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {
        try {
            $rows = @()
            foreach ($row in $script:Items.Select($script:BaseFilters[$StatusKey])) {
                $obj = [ordered]@{}
                foreach ($f in $script:VisibleFieldDefs) { $obj[$f.Key] = $row[$f.Key] }
                $rows += [PSCustomObject]$obj
            }
            $rows | Export-Csv -Path $sfd.FileName -NoTypeInformation -Encoding UTF8
            [System.Windows.Forms.MessageBox]::Show("Exported $($rows.Count) item(s).", "Export Complete", "OK", "Information") | Out-Null
        } catch {
            [System.Windows.Forms.MessageBox]::Show("Export failed: $($_.Exception.Message)", "Export Error", "OK", "Error") | Out-Null
        }
    }
}

# ---- Active Directory integration ----
# Matching is by trimmed, case-insensitive Last Name + First Name against AD Surname/GivenName.
# There's no username field in this app's schema, so this is inherently approximate - it's
# meant to surface candidates for human review (highlighting, review lists), never to move
# or delete anything on its own.

function Test-AdModuleAvailable {
    if (Get-Module -Name ActiveDirectory) { return $true }
    if (-not (Get-Module -ListAvailable -Name ActiveDirectory)) {
        [System.Windows.Forms.MessageBox]::Show(
            "The Active Directory PowerShell module isn't installed on this machine.`n`nInstall it via: RSAT: Active Directory Domain Services and LDS Tools.",
            "Active Directory Module Missing", "OK", "Warning") | Out-Null
        return $false
    }
    try {
        Import-Module ActiveDirectory -ErrorAction Stop
        return $true
    } catch {
        [System.Windows.Forms.MessageBox]::Show("Could not load the Active Directory module: $($_.Exception.Message)", "Active Directory Error", "OK", "Error") | Out-Null
        return $false
    }
}

# Alternate AD credentials for this session only - never written to disk, cleared on exit.
# Every AD cmdlet call splats in Get-AdCredParams so it transparently uses these when set,
# or falls back to the current Windows session's own rights otherwise.
$script:AdCredential = $null

function Get-AdCredParams {
    if ($null -ne $script:AdCredential) { return @{ Credential = $script:AdCredential } }
    return @{}
}

function Get-SanitizedAccountNameBase([string]$LastName, [string]$FirstName) {
    # Confirmed convention: last name + first initial, lowercase. SamAccountName has a
    # restricted character set, so anything that isn't a-z/0-9 (apostrophes, hyphens, spaces in
    # names like O'Brien or Smith-Jones) is stripped rather than passed through.
    $lastClean = ($LastName -replace '[^a-zA-Z0-9]', '').ToLowerInvariant()
    $firstInitial = if ($FirstName.Length -gt 0) { $FirstName.Substring(0, 1) -replace '[^a-zA-Z0-9]', '' } else { "" }
    return ($lastClean + $firstInitial.ToLowerInvariant())
}

function Get-UniqueSamAccountName([string]$BaseName, [hashtable]$AdParams) {
    # Fetches every existing SamAccountName once (cheap relative to a per-candidate AD round
    # trip, and matches the bulk-fetch pattern already used for the AD sync check) and appends
    # an incrementing digit on collision - checked regardless of the existing account's Enabled
    # state, since a disabled account still occupies that name.
    $existing = New-Object System.Collections.Generic.HashSet[string]
    try {
        Get-ADUser @AdParams -Filter * -Properties SamAccountName | ForEach-Object {
            [void]$existing.Add("$($_.SamAccountName)".ToLowerInvariant())
        }
    } catch {
        throw
    }
    if (-not $existing.Contains($BaseName.ToLowerInvariant())) { return $BaseName }
    $suffix = 2
    while ($existing.Contains("$BaseName$suffix".ToLowerInvariant())) { $suffix++ }
    return "$BaseName$suffix"
}

function Show-AdCredentialDialog {
    $dlg = New-Object System.Windows.Forms.Form
    $dlg.Text = "Active Directory Credentials"
    if ($script:AppIcon) { $dlg.Icon = $script:AppIcon }
    $dlg.Size = New-Object System.Drawing.Size(400, 255)
    $dlg.StartPosition = "CenterParent"
    $dlg.FormBorderStyle = "FixedDialog"
    $dlg.MaximizeBox = $false
    $dlg.MinimizeBox = $false

    $lblInfo = New-Object System.Windows.Forms.Label
    $lblInfo.Text = "Enter credentials for an account with rights to modify Active Directory (e.g. sujohnsmith), so AD updates don't fail mid-session. Used only in this app, only this session, never saved to disk. Cancel to use your normal Windows session instead."
    $lblInfo.Location = New-Object System.Drawing.Point(10, 10)
    $lblInfo.Size = New-Object System.Drawing.Size(365, 70)
    $dlg.Controls.Add($lblInfo)

    $lblUser = New-Object System.Windows.Forms.Label
    $lblUser.Text = "Username:"
    $lblUser.Location = New-Object System.Drawing.Point(10, 90)
    $lblUser.Size = New-Object System.Drawing.Size(90, 20)
    $dlg.Controls.Add($lblUser)

    $txtUser = New-Object System.Windows.Forms.TextBox
    $txtUser.Location = New-Object System.Drawing.Point(105, 87)
    $txtUser.Size = New-Object System.Drawing.Size(270, 20)
    if ($null -ne $script:AdCredential) { $txtUser.Text = $script:AdCredential.UserName }
    $dlg.Controls.Add($txtUser)

    $lblPass = New-Object System.Windows.Forms.Label
    $lblPass.Text = "Password:"
    $lblPass.Location = New-Object System.Drawing.Point(10, 120)
    $lblPass.Size = New-Object System.Drawing.Size(90, 20)
    $dlg.Controls.Add($lblPass)

    $txtPass = New-Object System.Windows.Forms.TextBox
    $txtPass.Location = New-Object System.Drawing.Point(105, 117)
    $txtPass.Size = New-Object System.Drawing.Size(270, 20)
    $txtPass.UseSystemPasswordChar = $true
    $dlg.Controls.Add($txtPass)

    $btnOK = New-Object System.Windows.Forms.Button
    $btnOK.Text = "OK"
    $btnOK.Location = New-Object System.Drawing.Point(205, 165)
    $btnOK.DialogResult = [System.Windows.Forms.DialogResult]::OK
    $dlg.Controls.Add($btnOK)

    $btnCancel = New-Object System.Windows.Forms.Button
    $btnCancel.Text = "Cancel"
    $btnCancel.Location = New-Object System.Drawing.Point(290, 165)
    $btnCancel.DialogResult = [System.Windows.Forms.DialogResult]::Cancel
    $dlg.Controls.Add($btnCancel)

    $dlg.AcceptButton = $btnOK
    $dlg.CancelButton = $btnCancel

    $result = $dlg.ShowDialog()
    if ($result -eq [System.Windows.Forms.DialogResult]::OK) {
        $username = $txtUser.Text.Trim()
        if ([string]::IsNullOrWhiteSpace($username)) {
            [System.Windows.Forms.MessageBox]::Show("Username is required.", "Validation Error", "OK", "Warning") | Out-Null
            $dlg.Dispose()
            return
        }
        if ($username -notmatch '[\\@]') {
            try {
                $netbios = (Get-ADDomain -ErrorAction Stop).NetBIOSName
                $username = "$netbios\$username"
            } catch { }
        }
        $securePass = ConvertTo-SecureString $txtPass.Text -AsPlainText -Force
        $script:AdCredential = New-Object System.Management.Automation.PSCredential($username, $securePass)
        [System.Windows.Forms.MessageBox]::Show("Active Directory actions in this app will now run as '$username' until you clear it or close the app.", "AD Credentials Set", "OK", "Information") | Out-Null
    }
    $dlg.Dispose()
}

function Clear-AdCredential {
    if ($null -eq $script:AdCredential) {
        [System.Windows.Forms.MessageBox]::Show("No alternate AD credentials are set - already using your current Windows session.", "AD Credentials", "OK", "Information") | Out-Null
        return
    }
    $script:AdCredential = $null
    [System.Windows.Forms.MessageBox]::Show("Cleared. Active Directory actions will use your current Windows session again.", "AD Credentials Cleared", "OK", "Information") | Out-Null
}

# ============================================================================
# ACCESS GATE - runs once at startup, before any inventory data is loaded or any window is
# shown. Two independent AD checks must both pass or the process exits immediately:
#   1. Gate account (Invoke-AccessGate): a single, dedicated AD account whose only purpose is
#      "is this operator allowed to even launch the tool at all." It should carry no AD rights
#      beyond existing - this is a passphrase check, not a privilege check.
#   2. SU login (Invoke-SuLogin): the operator's own AD account, which must belong to
#      $script:SuGroupName (checked transitively - nested membership counts). Its credential
#      becomes $script:AdCredential for the rest of the session, so every AD write this app
#      makes from here on runs as that account via the existing Get-AdCredParams, never as
#      whoever happens to be logged into Windows.
# Each failed attempt here is a real AD credential validation, so the domain's own account
# lockout policy applies exactly as it would to any other logon - the attempt cap below is a
# courtesy against fat-fingering, not the real brute-force defense.
# ============================================================================

function Test-AdCredentialValid([string]$Domain, [string]$Username, [string]$Password) {
    try {
        $ctx = New-Object System.DirectoryServices.AccountManagement.PrincipalContext(
            [System.DirectoryServices.AccountManagement.ContextType]::Domain, $Domain)
        try {
            return $ctx.ValidateCredentials($Username, $Password)
        } finally {
            $ctx.Dispose()
        }
    } catch {
        return $false
    }
}

function Test-SuGroupMembership([string]$Domain, [string]$Username, [string]$GroupName) {
    try {
        $ctx = New-Object System.DirectoryServices.AccountManagement.PrincipalContext(
            [System.DirectoryServices.AccountManagement.ContextType]::Domain, $Domain)
        try {
            $user = [System.DirectoryServices.AccountManagement.UserPrincipal]::FindByIdentity($ctx, $Username)
            if ($null -eq $user) { return $false }
            $group = [System.DirectoryServices.AccountManagement.GroupPrincipal]::FindByIdentity($ctx, $GroupName)
            if ($null -eq $group) { return $false }
            return $user.IsMemberOf($group)
        } finally {
            $ctx.Dispose()
        }
    } catch {
        return $false
    }
}

function Show-CredentialPromptDialog([string]$Title, [string]$InfoText, [string]$FixedUsername) {
    # $FixedUsername locks/pre-fills the username field (used for the gate account, which is
    # always the same one account) - pass $null to let the operator type their own username
    # (used for the SU login, which is a different person each time).
    $dlg = New-Object System.Windows.Forms.Form
    $dlg.Text = $Title
    if ($script:AppIcon) { $dlg.Icon = $script:AppIcon }
    $dlg.Size = New-Object System.Drawing.Size(420, 260)
    $dlg.StartPosition = "CenterScreen"
    $dlg.FormBorderStyle = "FixedDialog"
    $dlg.MaximizeBox = $false
    $dlg.MinimizeBox = $false
    $dlg.TopMost = $true

    $lblInfo = New-Object System.Windows.Forms.Label
    $lblInfo.Text = $InfoText
    $lblInfo.Location = New-Object System.Drawing.Point(10, 10)
    $lblInfo.Size = New-Object System.Drawing.Size(385, 60)
    $dlg.Controls.Add($lblInfo)

    $lblUser = New-Object System.Windows.Forms.Label
    $lblUser.Text = "Username:"
    $lblUser.Location = New-Object System.Drawing.Point(10, 80)
    $lblUser.Size = New-Object System.Drawing.Size(90, 20)
    $dlg.Controls.Add($lblUser)

    $txtUser = New-Object System.Windows.Forms.TextBox
    $txtUser.Location = New-Object System.Drawing.Point(105, 77)
    $txtUser.Size = New-Object System.Drawing.Size(285, 20)
    if ($FixedUsername) {
        $txtUser.Text = $FixedUsername
        $txtUser.ReadOnly = $true
        $txtUser.TabStop = $false
    }
    $dlg.Controls.Add($txtUser)

    $lblPass = New-Object System.Windows.Forms.Label
    $lblPass.Text = "Password:"
    $lblPass.Location = New-Object System.Drawing.Point(10, 110)
    $lblPass.Size = New-Object System.Drawing.Size(90, 20)
    $dlg.Controls.Add($lblPass)

    $txtPass = New-Object System.Windows.Forms.TextBox
    $txtPass.Location = New-Object System.Drawing.Point(105, 107)
    $txtPass.Size = New-Object System.Drawing.Size(285, 20)
    $txtPass.UseSystemPasswordChar = $true
    $dlg.Controls.Add($txtPass)

    $btnOK = New-Object System.Windows.Forms.Button
    $btnOK.Text = "Login"
    $btnOK.Location = New-Object System.Drawing.Point(220, 165)
    $btnOK.DialogResult = [System.Windows.Forms.DialogResult]::OK
    $dlg.Controls.Add($btnOK)

    $btnCancel = New-Object System.Windows.Forms.Button
    $btnCancel.Text = "Cancel"
    $btnCancel.Location = New-Object System.Drawing.Point(310, 165)
    $btnCancel.DialogResult = [System.Windows.Forms.DialogResult]::Cancel
    $dlg.Controls.Add($btnCancel)

    $dlg.AcceptButton = $btnOK
    $dlg.CancelButton = $btnCancel

    $result = $dlg.ShowDialog()
    $out = $null
    if ($result -eq [System.Windows.Forms.DialogResult]::OK) {
        $out = [PSCustomObject]@{ Username = $txtUser.Text.Trim(); Password = $txtPass.Text }
    }
    $dlg.Dispose()
    return $out
}

function Invoke-AccessGate {
    for ($attempt = 1; $attempt -le $script:MaxLoginAttempts; $attempt++) {
        $cred = Show-CredentialPromptDialog -Title "Inventory Manager - Access Gate" `
            -InfoText "This tool is restricted. Enter the password for the designated gate account to continue." `
            -FixedUsername $script:InventoryGateAccountSam
        if ($null -eq $cred) { return $false }
        if (Test-AdCredentialValid -Domain $script:AdDomainFqdn -Username $cred.Username -Password $cred.Password) {
            return $true
        }
        [System.Windows.Forms.MessageBox]::Show("Incorrect password. Attempt $attempt of $($script:MaxLoginAttempts).", "Access Denied", "OK", "Error") | Out-Null
    }
    return $false
}

function Invoke-SuLogin {
    for ($attempt = 1; $attempt -le $script:MaxLoginAttempts; $attempt++) {
        $cred = Show-CredentialPromptDialog -Title "Inventory Manager - Admin Login" `
            -InfoText "Enter your own AD admin account credentials. Your account must be a member of '$($script:SuGroupName)' to use this tool." `
            -FixedUsername $null
        if ($null -eq $cred) { return $false }
        if ([string]::IsNullOrWhiteSpace($cred.Username)) {
            [System.Windows.Forms.MessageBox]::Show("Username is required.", "Access Denied", "OK", "Error") | Out-Null
            continue
        }
        if (-not (Test-AdCredentialValid -Domain $script:AdDomainFqdn -Username $cred.Username -Password $cred.Password)) {
            [System.Windows.Forms.MessageBox]::Show("Incorrect username or password. Attempt $attempt of $($script:MaxLoginAttempts).", "Access Denied", "OK", "Error") | Out-Null
            continue
        }
        if (-not (Test-SuGroupMembership -Domain $script:AdDomainFqdn -Username $cred.Username -GroupName $script:SuGroupName)) {
            [System.Windows.Forms.MessageBox]::Show("'$($cred.Username)' is a valid AD account but is not a member of '$($script:SuGroupName)'. Access denied.", "Access Denied", "OK", "Error") | Out-Null
            continue
        }
        $userForCred = $cred.Username
        if ($userForCred -notmatch '[\\@]') {
            try {
                $netbios = (Get-ADDomain -ErrorAction Stop).NetBIOSName
                $userForCred = "$netbios\$userForCred"
            } catch { }
        }
        $securePass = ConvertTo-SecureString $cred.Password -AsPlainText -Force
        $script:AdCredential = New-Object System.Management.Automation.PSCredential($userForCred, $securePass)
        $script:SuUsername = $cred.Username
        return $true
    }
    return $false
}

if (-not (Invoke-AccessGate)) {
    [System.Windows.Forms.MessageBox]::Show("Access gate check failed or was cancelled. Inventory Manager will now close.", "Inventory Manager", "OK", "Error") | Out-Null
    exit 1
}
# The SU login (Invoke-SuLogin) happens later, in $form.Add_Shown, after the main window and
# its data are already on screen - only the gate check above blocks the window from appearing
# at all.

function Get-InventoryMatchesForName([string]$LastName, [string]$FirstName) {
    $ln = "$LastName".Trim().Replace("'", "''")
    $fn = "$FirstName".Trim().Replace("'", "''")
    if ([string]::IsNullOrWhiteSpace($ln) -or [string]::IsNullOrWhiteSpace($fn)) { return @() }
    return @($script:Items.Select("[LastName] = '$ln' AND [FirstName] = '$fn'"))
}

function Invoke-AdUserMatchCheck {
    # Classifies every non-Terminated, non-Off-Domain inventory row against AD on two
    # independent axes, each with its own dedicated tab (computer wins ties, since a computer
    # that isn't in AD at all is a stronger signal than a name that doesn't resolve):
    # 1. The COMPUTER object itself (by Device Name) - does an AD computer object with this
    #    name exist anywhere under the Domain Computers OU at all (Get-ADComputer's default
    #    search scope is the full subtree, so Desktop/Laptop/Disabled sub-OUs are all already
    #    included)? If not -> auto-moved to "Computer Not in AD". Separately, ADComputerDisabled
    #    flags (and, via the block below, auto-moves to Disabled Devices) a computer that DOES
    #    exist but is disabled or parked in the Disabled Computers OU - that's a different
    #    problem (decommissioned, not missing) and always takes final priority if both apply.
    # 2. The assigned USER (by Last+First Name, or by the anchored Username from "Match User"):
    #    matched+enabled (flag cleared), matched+disabled ("AD account disabled" - the account
    #    exists, just not active), or no matching AD user at all ("No matching AD user found" ->
    #    auto-moved to "User Not in AD", but only when the computer itself checked out fine).
    #    Rows with no name to match (e.g. unassigned stock devices) are skipped entirely.
    # Off-Domain devices are excluded from both auto-moves - they're already called out
    # separately by the Off Domain view, and being off-domain is itself a plausible reason a
    # device or its user wouldn't resolve cleanly against this domain's AD (most off-domain
    # machines were never joined here at all), so folding them in here would just be noise.
    if (-not (Test-AdModuleAvailable)) { return $null }
    $adParams = Get-AdCredParams
    try {
        $allUsers = @(Get-ADUser @adParams -Filter * -Properties GivenName, Surname, Enabled)
        $allComputers = @(Get-ADComputer @adParams -SearchBase $script:ComputersOuDn -Filter * -Properties Name, Enabled, DistinguishedName)
    } catch {
        [System.Windows.Forms.MessageBox]::Show("Could not query Active Directory: $($_.Exception.Message)", "Active Directory Error", "OK", "Error") | Out-Null
        return $null
    }

    $enabledKeys = New-Object System.Collections.Generic.HashSet[string]
    $allKeys = New-Object System.Collections.Generic.HashSet[string]
    $usersBySam = @{}
    foreach ($u in $allUsers) {
        if (-not [string]::IsNullOrWhiteSpace("$($u.SamAccountName)")) { $usersBySam[$u.SamAccountName.ToLowerInvariant()] = $u }
        if ([string]::IsNullOrWhiteSpace($u.Surname) -or [string]::IsNullOrWhiteSpace($u.GivenName)) { continue }
        $key = ($u.Surname.Trim() + "|" + $u.GivenName.Trim()).ToLowerInvariant()
        [void]$allKeys.Add($key)
        if ($u.Enabled) { [void]$enabledKeys.Add($key) }
    }

    $disabledOuSuffix = ",$($script:DisabledComputersOuDn)".ToLowerInvariant()
    $disabledComputerNames = New-Object System.Collections.Generic.HashSet[string]
    $allComputerNames = New-Object System.Collections.Generic.HashSet[string]
    foreach ($c in $allComputers) {
        $cName = "$($c.Name)".Trim().ToLowerInvariant()
        if (-not [string]::IsNullOrWhiteSpace($cName)) { [void]$allComputerNames.Add($cName) }
        $inDisabledOu = "$($c.DistinguishedName)".ToLowerInvariant().EndsWith($disabledOuSuffix)
        if ($inDisabledOu -or -not $c.Enabled) {
            [void]$disabledComputerNames.Add($cName)
        }
    }

    $disabledCount = 0
    $notFoundCount = 0
    $computerNotFoundCount = 0
    $computerDisabledCount = 0
    $autoMovedCount = 0
    $autoMovedComputerNotInAdCount = 0
    $autoMovedUserNotInAdCount = 0
    foreach ($row in $script:Items.Select("[Status] <> 'Terminated'")) {
        $username = "$($row.Username)".Trim()
        if (-not [string]::IsNullOrWhiteSpace($username)) {
            # Anchored via "Match User" - authoritative, and self-healing: pulls the current
            # First/Last Name from AD every time this check runs, so a name change in AD
            # (marriage, correction, etc.) automatically reflects here without breaking the
            # match the way a pure name-based lookup would.
            $adUser = $usersBySam[$username.ToLowerInvariant()]
            if ($null -ne $adUser) {
                $row["FirstName"] = "$($adUser.GivenName)"
                $row["LastName"] = "$($adUser.Surname)"
                if ($adUser.Enabled) { $row["ADFlagReason"] = "" }
                else { $row["ADFlagReason"] = "AD account disabled"; $disabledCount++ }
            } else {
                $row["ADFlagReason"] = "No matching AD user found"; $notFoundCount++
            }
        } else {
            $key = ("$($row.LastName)".Trim() + "|" + "$($row.FirstName)".Trim()).ToLowerInvariant()
            if ($key -eq "|") { $row["ADFlagReason"] = "" }
            elseif ($enabledKeys.Contains($key)) { $row["ADFlagReason"] = "" }
            elseif ($allKeys.Contains($key)) { $row["ADFlagReason"] = "AD account disabled"; $disabledCount++ }
            else { $row["ADFlagReason"] = "No matching AD user found"; $notFoundCount++ }
        }

        $deviceKey = "$($row.DeviceName)".Trim().ToLowerInvariant()
        $isOffDomain = "$($row.Domain)".Trim() -eq "Off-Domain"
        $computerExists = [string]::IsNullOrWhiteSpace($deviceKey) -or $allComputerNames.Contains($deviceKey)
        if ($computerExists) { $row["ADComputerNotFound"] = "" }
        else { $row["ADComputerNotFound"] = "No matching AD computer object found"; $computerNotFoundCount++ }

        # Auto-move into Computer Not in AD / User Not in AD, per the user's explicit request,
        # and the computer axis wins if both fail on the same device (see the priority note in
        # this function's header comment). Rows already in Computer Not in AD or Disabled
        # Devices are left alone - those are the two "final" classifications in this hierarchy.
        # Rows already in User Not in AD stay ELIGIBLE for re-evaluation on every run, though -
        # that's what lets a device get correctly promoted to Computer Not in AD if it turns out
        # its computer object is missing too (this matters for anything already misclassified by
        # the earlier version of this check, which only ever looked at the user, never the
        # computer).
        if (-not $isOffDomain -and "$($row.Status)" -notin @("ComputerNotInAD", "DisabledDevices")) {
            if (-not $computerExists) {
                $row["Status"] = "ComputerNotInAD"
                $autoMovedComputerNotInAdCount++
            } elseif ("$($row.ADFlagReason)" -eq "No matching AD user found") {
                $row["Status"] = "NotInAD"
                $autoMovedUserNotInAdCount++
            }
        }

        if (-not [string]::IsNullOrWhiteSpace($deviceKey) -and $disabledComputerNames.Contains($deviceKey)) {
            $row["ADComputerDisabled"] = "Computer object disabled in AD"
            $computerDisabledCount++
            # Auto-move into the Disabled Devices tab, per the user's explicit request - only
            # once (moving a row already there is a harmless no-op check, not a re-move).
            if ("$($row.Status)" -ne "DisabledDevices") {
                $row["Status"] = "DisabledDevices"
                $autoMovedCount++
            }
        } else {
            # Computer is fine now (or was never disabled). If it had been auto-moved here
            # previously and its computer got re-enabled, leave it in Disabled Devices for a
            # human to move back on purpose - we don't know its original tab to restore it to.
            $row["ADComputerDisabled"] = ""
        }
    }
    Save-Inventory
    Refresh-AllTabs
    return [PSCustomObject]@{
        TotalAdUsers = $allUsers.Count
        DisabledDeviceCount = $disabledCount
        NotFoundDeviceCount = $notFoundCount
        ComputerNotFoundCount = $computerNotFoundCount
        AutoMovedCount = $autoMovedCount
        AutoMovedComputerNotInAdCount = $autoMovedComputerNotInAdCount
        AutoMovedUserNotInAdCount = $autoMovedUserNotInAdCount
        ComputerDisabledCount = $computerDisabledCount
    }
}

function Invoke-AdCheckForSelected([string]$StatusKey) {
    # A scoped, on-demand version of Invoke-AdUserMatchCheck's classification for just the
    # selected device(s), instead of pulling every AD user/computer to check the whole
    # inventory - useful for spot-checking one device right after fixing something in AD,
    # without waiting on (or triggering credentials for) a full sync. Applies the exact same
    # priority rules: computer-not-found beats user-not-found, and a disabled computer object
    # always wins and takes the device to Disabled Devices regardless of domain.
    $grid = $script:Grids[$StatusKey]
    if ($grid.SelectedRows.Count -eq 0) {
        [System.Windows.Forms.MessageBox]::Show("Select one or more devices to check.", "No Selection", "OK", "Information") | Out-Null
        return
    }
    if (-not (Test-AdModuleAvailable)) { return }

    $ids = @($grid.SelectedRows | Sort-Object Index | ForEach-Object { $_.DataBoundItem["Id"] })
    $adParams = Get-AdCredParams
    $disabledOuSuffix = ",$($script:DisabledComputersOuDn)".ToLowerInvariant()

    $checkedCount = 0
    $movedCount = 0
    $errorCount = 0

    foreach ($id in $ids) {
        $dataRow = $script:Items.Rows.Find($id)
        if ($null -eq $dataRow) { continue }
        $deviceName = "$($dataRow.DeviceName)".Trim()
        $username = "$($dataRow.Username)".Trim()
        $lastName = "$($dataRow.LastName)".Trim()
        $firstName = "$($dataRow.FirstName)".Trim()
        $isOffDomain = "$($dataRow.Domain)".Trim() -eq "Off-Domain"

        try {
            # ---- User axis: same resolution order as Match User (Username anchor first, then
            # Last Name search narrowed by First Name, then the picker dialog if still ambiguous) ----
            $adUser = $null
            $userChecked = $false
            if (-not [string]::IsNullOrWhiteSpace($username)) {
                $userChecked = $true
                try { $adUser = Get-ADUser @adParams -Identity $username -Properties GivenName, Surname, Enabled -ErrorAction Stop } catch { $adUser = $null }
            } elseif (-not [string]::IsNullOrWhiteSpace($lastName)) {
                $userChecked = $true
                $safeLast = $lastName.Replace("'", "''")
                $candidates = @(Get-ADUser @adParams -Filter "Surname -eq '$safeLast'" -Properties GivenName, Surname, Enabled -ErrorAction Stop)
                if ($candidates.Count -eq 1) {
                    $adUser = $candidates[0]
                } elseif ($candidates.Count -gt 1) {
                    if (-not [string]::IsNullOrWhiteSpace($firstName)) {
                        $narrowed = @($candidates | Where-Object { "$($_.GivenName)".Trim().ToLowerInvariant() -eq $firstName.ToLowerInvariant() })
                        if ($narrowed.Count -eq 1) { $adUser = $narrowed[0] }
                    }
                    if ($null -eq $adUser) {
                        $picked = Show-AdUserPickerDialog -Candidates $candidates -DeviceName $deviceName
                        if ($null -ne $picked) { $adUser = $picked }
                    }
                }
            }

            if ($userChecked) {
                if ($null -ne $adUser) {
                    $dataRow["FirstName"] = "$($adUser.GivenName)"
                    $dataRow["LastName"] = "$($adUser.Surname)"
                    if ($adUser.Enabled) { $dataRow["ADFlagReason"] = "" }
                    else { $dataRow["ADFlagReason"] = "AD account disabled" }
                } else {
                    $dataRow["ADFlagReason"] = "No matching AD user found"
                }
            } else {
                $dataRow["ADFlagReason"] = ""
            }

            # ---- Computer axis: a single targeted lookup instead of pulling every computer ----
            $adComputer = $null
            if (-not [string]::IsNullOrWhiteSpace($deviceName)) {
                $safeDevice = $deviceName.Replace("'", "''")
                $adComputer = Get-ADComputer @adParams -SearchBase $script:ComputersOuDn -Filter "Name -eq '$safeDevice'" -Properties Enabled, DistinguishedName -ErrorAction Stop |
                    Select-Object -First 1
            }
            $computerExists = [string]::IsNullOrWhiteSpace($deviceName) -or ($null -ne $adComputer)
            if ($computerExists) { $dataRow["ADComputerNotFound"] = "" }
            else { $dataRow["ADComputerNotFound"] = "No matching AD computer object found" }

            $computerDisabled = ($null -ne $adComputer) -and (-not $adComputer.Enabled -or "$($adComputer.DistinguishedName)".ToLowerInvariant().EndsWith($disabledOuSuffix))

            # ---- Same auto-move priority as the bulk check ----
            if (-not $isOffDomain -and "$($dataRow.Status)" -notin @("ComputerNotInAD", "DisabledDevices")) {
                if (-not $computerExists) {
                    $dataRow["Status"] = "ComputerNotInAD"; $movedCount++
                } elseif ("$($dataRow.ADFlagReason)" -eq "No matching AD user found") {
                    $dataRow["Status"] = "NotInAD"; $movedCount++
                }
            }
            if ($computerDisabled) {
                $dataRow["ADComputerDisabled"] = "Computer object disabled in AD"
                if ("$($dataRow.Status)" -ne "DisabledDevices") { $dataRow["Status"] = "DisabledDevices"; $movedCount++ }
            } else {
                $dataRow["ADComputerDisabled"] = ""
            }

            $userSummary = if (-not $userChecked) { "n/a" } elseif ($null -ne $adUser) { "matched" } else { "not found" }
            $compSummary = if ($computerExists) { "found" } else { "not found" }
            Write-AppLog "Check AD - '$deviceName': user $userSummary, computer $compSummary, now in '$($dataRow.Status)'." "Info"
            $checkedCount++
        } catch {
            Write-AppLog "Check AD - '$deviceName': AD lookup failed: $($_.Exception.Message)" "Error"
            $errorCount++
        }
    }

    Save-Inventory
    Refresh-AllTabs
    [System.Windows.Forms.MessageBox]::Show(
        "Checked $checkedCount device(s) against AD.`n$movedCount moved to a different tab based on the result, $errorCount errored.`n`nSee the Activity Log for details.",
        "Check AD Complete", "OK", "Information") | Out-Null
}

function Get-AdRecentUserChanges([int]$Days = 30) {
    if (-not (Test-AdModuleAvailable)) { return @() }
    $adParams = Get-AdCredParams
    $cutoff = (Get-Date).AddDays(-$Days)
    try {
        $users = @(Get-ADUser @adParams -Filter { whenChanged -ge $cutoff } -Properties GivenName, Surname, Enabled, whenChanged, whenCreated |
            Sort-Object whenChanged -Descending)
    } catch {
        [System.Windows.Forms.MessageBox]::Show("Could not query Active Directory: $($_.Exception.Message)", "Active Directory Error", "OK", "Error") | Out-Null
        return @()
    }
    $results = @()
    foreach ($u in $users) {
        $matches = @(Get-InventoryMatchesForName -LastName $u.Surname -FirstName $u.GivenName)
        $matchText = if ($matches.Count -eq 0) {
            "No matching device"
        } else {
            ($matches | ForEach-Object {
                $matchRow = $_
                $statusLabel = ($script:Statuses | Where-Object { $_.Key -eq $matchRow.Status }).Label
                "$($matchRow.DeviceName) ($statusLabel)"
            }) -join "; "
        }
        $results += [PSCustomObject]@{
            Name         = "$($u.GivenName) $($u.Surname)"
            Enabled      = $u.Enabled
            WhenChanged  = $u.whenChanged
            WhenCreated  = $u.whenCreated
            MatchedCount = $matches.Count
            MatchText    = $matchText
        }
    }
    return @($results)
}

function Get-AdComputersNotInInventory {
    if (-not (Test-AdModuleAvailable)) { return @() }
    $adParams = Get-AdCredParams
    try {
        $adComputers = @(Get-ADComputer @adParams -SearchBase $script:ComputersOuDn -Filter { Enabled -eq $true } -Properties Name)
    } catch {
        [System.Windows.Forms.MessageBox]::Show("Could not query Active Directory: $($_.Exception.Message)", "Active Directory Error", "OK", "Error") | Out-Null
        return @()
    }
    $inventoryNames = New-Object System.Collections.Generic.HashSet[string]
    foreach ($row in $script:Items.Rows) {
        $dn = "$($row.DeviceName)".Trim().ToLowerInvariant()
        if (-not [string]::IsNullOrWhiteSpace($dn)) { [void]$inventoryNames.Add($dn) }
    }
    $missing = @()
    foreach ($c in $adComputers) {
        if (-not $inventoryNames.Contains($c.Name.Trim().ToLowerInvariant())) {
            $missing += [PSCustomObject]@{ ComputerName = $c.Name }
        }
    }
    return @($missing | Sort-Object ComputerName)
}

function Get-AdUsersWithNoDevice {
    if (-not (Test-AdModuleAvailable)) { return @() }
    $adParams = Get-AdCredParams
    try {
        $adUsers = @(Get-ADUser @adParams -Filter { Enabled -eq $true } -Properties GivenName, Surname)
    } catch {
        [System.Windows.Forms.MessageBox]::Show("Could not query Active Directory: $($_.Exception.Message)", "Active Directory Error", "OK", "Error") | Out-Null
        return @()
    }
    # A user counts as "has a device" if any inventory row is anchored to them by Username
    # (the authoritative Match User anchor), or - for rows never run through Match User yet -
    # by a Last+First Name match. Terminated and IT Stock rows don't count: a Terminated row's
    # name belongs to whoever HAD the device, not a current assignment, and IT Stock is
    # explicitly unassigned bench stock.
    $usernamesWithDevice = New-Object System.Collections.Generic.HashSet[string]
    $namesWithDevice = New-Object System.Collections.Generic.HashSet[string]
    foreach ($row in $script:Items.Select("[Status] <> 'Terminated' AND [Status] <> 'ITStock'")) {
        $u = "$($row.Username)".Trim()
        if (-not [string]::IsNullOrWhiteSpace($u)) { [void]$usernamesWithDevice.Add($u.ToLowerInvariant()) }
        $ln = "$($row.LastName)".Trim(); $fn = "$($row.FirstName)".Trim()
        if (-not [string]::IsNullOrWhiteSpace($ln) -and -not [string]::IsNullOrWhiteSpace($fn)) {
            [void]$namesWithDevice.Add(($ln + "|" + $fn).ToLowerInvariant())
        }
    }
    $noDevice = @()
    foreach ($u in $adUsers) {
        # Skip AD users with neither name field set - almost always a service/system account
        # rather than a real person who'd need a device tracked in inventory.
        if ([string]::IsNullOrWhiteSpace($u.GivenName) -and [string]::IsNullOrWhiteSpace($u.Surname)) { continue }
        $sam = "$($u.SamAccountName)".ToLowerInvariant()
        if ($usernamesWithDevice.Contains($sam)) { continue }
        $key = ("$($u.Surname)".Trim() + "|" + "$($u.GivenName)".Trim()).ToLowerInvariant()
        if ($namesWithDevice.Contains($key)) { continue }
        $noDevice += [PSCustomObject]@{ Username = $u.SamAccountName; LastName = $u.Surname; FirstName = $u.GivenName }
    }
    return @($noDevice | Sort-Object LastName, FirstName)
}

function Sync-AdComputerDescription([string]$DeviceName, [string]$Description) {
    if ([string]::IsNullOrWhiteSpace($DeviceName)) { return }
    if (-not (Test-AdModuleAvailable)) { return }
    $adParams = Get-AdCredParams
    try {
        # -Description "" does not reliably clear the attribute (confirmed by a real failure -
        # AD rejected it while the exact same write with a real value, moments earlier, worked
        # fine) - -Clear is the correct way to remove a single-valued attribute's value entirely.
        if ([string]::IsNullOrEmpty($Description)) {
            Set-ADComputer @adParams -Identity $DeviceName -Clear Description -ErrorAction Stop
        } else {
            Set-ADComputer @adParams -Identity $DeviceName -Description $Description -ErrorAction Stop
        }
        Write-AppLog "AD computer '$DeviceName' Description updated to match." "Info"
    } catch {
        Write-AppLog "Saved locally, but AD was NOT updated for '$DeviceName': $($_.Exception.Message)" "Error"
    }
}

function Set-AdComputerDisabled([string]$DeviceName) {
    if ([string]::IsNullOrWhiteSpace($DeviceName)) { return }
    if (-not (Test-AdModuleAvailable)) { return }
    $adParams = Get-AdCredParams
    try {
        Set-ADComputer @adParams -Identity $DeviceName -Enabled $false -ErrorAction Stop
        Write-AppLog "AD computer '$DeviceName' disabled." "Warning"
    } catch {
        Write-AppLog "Saved locally, but AD computer '$DeviceName' could NOT be disabled: $($_.Exception.Message)" "Error"
    }
}

function Get-AdActorDisplayName([hashtable]$AdParams) {
    # Best-effort "First Last (username)" for whoever is logged in as SU right now - falls back
    # to the bare username if the lookup fails, since this is only ever used for a human-readable
    # audit trail (AD Notes entries), never a security decision.
    $display = "$($script:SuUsername)"
    try {
        $u = Get-ADUser @AdParams -Identity $script:SuUsername -Properties GivenName, Surname -ErrorAction Stop
        $display = "$($u.GivenName) $($u.Surname) ($($script:SuUsername))".Trim()
    } catch { }
    return $display
}

function Format-AdNoteEntry([string]$ActionText) {
    return "[$(Get-Date -Format 'yyyy-MM-dd HH:mm')] $ActionText"
}

function Add-AdUserNoteLine([string]$SamAccountName, [string]$ActionText, [hashtable]$AdParams) {
    # Appends one timestamped entry to the AD user's "info" attribute (the Notes box under
    # ADUC's Telephones tab) rather than overwriting it, so onboarding/transfer/offboarding
    # events accumulate into a running history on the account itself - visible to anyone who
    # opens the user's properties, no need to dig through this app's own logs.
    # "info" is schema-capped at 1024 Unicode characters - if the running history would exceed
    # a safe margin below that, the OLDEST entries are dropped first so the newest entry (the one
    # this call is actually here to add) is never the one that gets cut.
    $newEntry = Format-AdNoteEntry $ActionText
    $divider = "----------"
    $existing = $null
    try {
        $existing = (Get-ADUser @AdParams -Identity $SamAccountName -Properties info -ErrorAction Stop).info
    } catch {
        throw
    }
    $combined = if ([string]::IsNullOrWhiteSpace($existing)) { $newEntry } else { "$existing`r`n$divider`r`n$newEntry" }
    $maxLen = 1000
    if ($combined.Length -gt $maxLen) {
        $entries = @($combined -split "`r`n$([regex]::Escape($divider))`r`n")
        while ($entries.Count -gt 1 -and ($entries -join "`r`n$divider`r`n").Length -gt $maxLen) {
            $entries = $entries[1..($entries.Count - 1)]
        }
        $combined = $entries -join "`r`n$divider`r`n"
        if ($combined.Length -gt $maxLen) {
            # Even the single newest entry alone doesn't fit (pathological) - keep the tail end
            # of it rather than fail the write entirely.
            $combined = $combined.Substring($combined.Length - $maxLen)
        }
    }
    Set-ADUser @AdParams -Identity $SamAccountName -Replace @{ info = $combined } -ErrorAction Stop
}

function Show-TicketNumberDialog([string]$DefaultValue, [string]$PromptText = "Enter the ticket number for this disable - it will be stamped into the computer's AD Description as 'Disabled per ticket # <number>'.") {
    $dlg = New-Object System.Windows.Forms.Form
    $dlg.Text = "Ticket Number"
    if ($script:AppIcon) { $dlg.Icon = $script:AppIcon }
    $dlg.Size = New-Object System.Drawing.Size(380, 165)
    $dlg.StartPosition = "CenterParent"
    $dlg.FormBorderStyle = "FixedDialog"
    $dlg.MaximizeBox = $false
    $dlg.MinimizeBox = $false

    $lbl = New-Object System.Windows.Forms.Label
    $lbl.Text = $PromptText
    $lbl.Location = New-Object System.Drawing.Point(10, 10)
    $lbl.Size = New-Object System.Drawing.Size(345, 45)
    $dlg.Controls.Add($lbl)

    $txt = New-Object System.Windows.Forms.TextBox
    $txt.Location = New-Object System.Drawing.Point(10, 60)
    $txt.Size = New-Object System.Drawing.Size(345, 20)
    if ($DefaultValue) { $txt.Text = $DefaultValue }
    $dlg.Controls.Add($txt)

    $btnOK = New-Object System.Windows.Forms.Button
    $btnOK.Text = "OK"
    $btnOK.Location = New-Object System.Drawing.Point(190, 95)
    $btnOK.DialogResult = [System.Windows.Forms.DialogResult]::OK
    $dlg.Controls.Add($btnOK)

    $btnCancel = New-Object System.Windows.Forms.Button
    $btnCancel.Text = "Cancel"
    $btnCancel.Location = New-Object System.Drawing.Point(275, 95)
    $btnCancel.DialogResult = [System.Windows.Forms.DialogResult]::Cancel
    $dlg.Controls.Add($btnCancel)

    $dlg.AcceptButton = $btnOK
    $dlg.CancelButton = $btnCancel
    # Control.Focus() returns a bool - if not voided, PowerShell silently adds it to this
    # function's return stream, turning "$ticketNumber" into a 2-element array that stringifies
    # as "False 000123" wherever it's used (confirmed by reproduction - caught this exact bug
    # via testing before it ever reached a real AD write).
    [void]$txt.Focus()

    $out = $null
    if ($dlg.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {
        $out = $txt.Text.Trim()
    }
    $dlg.Dispose()
    return $out
}

function Pull-AdDescriptionsForSelected([string]$StatusKey) {
    $grid = $script:Grids[$StatusKey]
    if ($grid.SelectedRows.Count -eq 0) {
        [System.Windows.Forms.MessageBox]::Show("Select one or more items to pull from AD.", "No Selection", "OK", "Information") | Out-Null
        return
    }
    if (-not (Test-AdModuleAvailable)) { return }

    $targets = @($grid.SelectedRows | Sort-Object Index | ForEach-Object {
        [PSCustomObject]@{ Id = $_.DataBoundItem["Id"]; DeviceName = "$($_.DataBoundItem["DeviceName"])" }
    })

    $confirmText = if ($targets.Count -eq 1) {
        "Pull the current AD Description for '$($targets[0].DeviceName)'? This will overwrite the Description field here with whatever is currently set in Active Directory."
    } else {
        "Pull the current AD Description for $($targets.Count) selected device(s)? This will overwrite the Description field here with whatever is currently set in Active Directory for each."
    }
    $confirm = [System.Windows.Forms.MessageBox]::Show($confirmText, "Confirm Pull from AD", "YesNo", "Question")
    if ($confirm -ne [System.Windows.Forms.DialogResult]::Yes) { return }

    $adParams = Get-AdCredParams
    $updated = 0
    $failures = @()
    foreach ($t in $targets) {
        if ([string]::IsNullOrWhiteSpace($t.DeviceName)) { continue }
        try {
            $adComputer = Get-ADComputer @adParams -Identity $t.DeviceName -Properties Description -ErrorAction Stop
            $dataRow = $script:Items.Rows.Find($t.Id)
            if ($null -ne $dataRow) {
                $dataRow["Description"] = "$($adComputer.Description)"
                $updated++
            }
        } catch {
            $failures += "$($t.DeviceName): $($_.Exception.Message)"
        }
    }

    if ($updated -gt 0) {
        Save-Inventory
        Refresh-AllTabs
    }

    $msg = "Pulled AD Description for $updated device(s)."
    if ($failures.Count -gt 0) {
        $shown = $failures | Select-Object -First 5
        $msg += "`n`n$($failures.Count) failed:`n" + ($shown -join "`n")
        if ($failures.Count -gt 5) { $msg += "`n...and $($failures.Count - 5) more." }
    }
    [System.Windows.Forms.MessageBox]::Show($msg, "Pull from AD Complete", "OK", "Information") | Out-Null
}

# Dispatches any field marked SyncToAd in $script:FieldDefs to its AD counterpart, but only
# when the value actually changed - OldValues pass $null for a brand-new item (Add), where
# anything non-blank counts as a change.
function Sync-AdFieldsAfterSave([string]$DeviceName, [hashtable]$OldValues, [hashtable]$NewValues) {
    foreach ($f in $script:FieldDefs) {
        if (-not $f.ContainsKey("SyncToAd") -or -not $f.SyncToAd) { continue }
        $newVal = "$($NewValues[$f.Key])".Trim()
        $oldVal = if ($null -ne $OldValues) { "$($OldValues[$f.Key])".Trim() } else { "" }
        if ($newVal -eq $oldVal) { continue }
        if ($f.Key -eq "Description") {
            Sync-AdComputerDescription -DeviceName $DeviceName -Description $newVal
        }
    }
}

function Show-AdUserPickerDialog([array]$Candidates, [string]$DeviceName) {
    $dlg = New-Object System.Windows.Forms.Form
    $dlg.Text = "Select AD User for '$DeviceName'"
    $dlg.Size = New-Object System.Drawing.Size(500, 350)
    $dlg.StartPosition = "CenterParent"
    $dlg.MinimumSize = New-Object System.Drawing.Size(400, 250)

    $grid = New-Object System.Windows.Forms.DataGridView
    $grid.Dock = "Fill"
    $grid.ReadOnly = $true
    $grid.AllowUserToAddRows = $false
    $grid.MultiSelect = $false
    $grid.SelectionMode = "FullRowSelect"
    $grid.AutoSizeColumnsMode = "Fill"
    $displayRows = @($Candidates | ForEach-Object { [PSCustomObject]@{ FirstName = $_.GivenName; LastName = $_.Surname; SamAccountName = $_.SamAccountName; Enabled = $_.Enabled } })
    $grid.DataSource = [System.Collections.ArrayList]$displayRows

    $bottomPanel = New-Object System.Windows.Forms.Panel
    $bottomPanel.Dock = "Bottom"
    $bottomPanel.Height = 45

    $btnSelect = New-Object System.Windows.Forms.Button
    $btnSelect.Text = "Select"
    $btnSelect.Location = New-Object System.Drawing.Point(310, 8)
    $btnSelect.Size = New-Object System.Drawing.Size(80, 28)
    $btnSelect.DialogResult = [System.Windows.Forms.DialogResult]::OK
    $bottomPanel.Controls.Add($btnSelect)

    $btnCancel = New-Object System.Windows.Forms.Button
    $btnCancel.Text = "Cancel"
    $btnCancel.Location = New-Object System.Drawing.Point(400, 8)
    $btnCancel.Size = New-Object System.Drawing.Size(80, 28)
    $btnCancel.DialogResult = [System.Windows.Forms.DialogResult]::Cancel
    $bottomPanel.Controls.Add($btnCancel)

    $dlg.Controls.Add($grid)
    $dlg.Controls.Add($bottomPanel)
    $dlg.AcceptButton = $btnSelect
    $dlg.CancelButton = $btnCancel

    $result = $null
    if ($dlg.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK -and $grid.SelectedRows.Count -eq 1) {
        $selectedSam = "$($grid.SelectedRows[0].Cells["SamAccountName"].Value)"
        $result = $Candidates | Where-Object { "$($_.SamAccountName)" -eq $selectedSam } | Select-Object -First 1
    }
    $dlg.Dispose()
    return $result
}

function Show-TransferTargetDialog([int]$ExcludeId = 0, [string]$Title = "Select Available Computer") {
    # Candidate replacements are ANY inventory device with a blank Description, regardless of
    # which tab it's sitting in (explicitly not limited to IT Stock) - a blank Description is
    # this app's existing signal that nobody's currently assigned to a device (Description gets
    # set to "<Full Name>***" the moment someone is), so it doubles as "available" here too.
    # Reused as-is by the Onboarding wizard's Computer Assignment step (same "available device"
    # pool, just a different caller and title) - $ExcludeId defaults to 0, which never matches a
    # real Id, since Onboarding has no "old device" to exclude the way Transfer does.
    $dlg = New-Object System.Windows.Forms.Form
    $dlg.Text = $Title
    $dlg.Size = New-Object System.Drawing.Size(700, 450)
    $dlg.StartPosition = "CenterParent"
    $dlg.MinimumSize = New-Object System.Drawing.Size(500, 300)

    $topPanel = New-Object System.Windows.Forms.Panel
    $topPanel.Dock = "Top"
    $topPanel.Height = 36

    $lblSearch = New-Object System.Windows.Forms.Label
    $lblSearch.Text = "Search:"
    $lblSearch.Location = New-Object System.Drawing.Point(10, 10)
    $lblSearch.Size = New-Object System.Drawing.Size(50, 20)
    $topPanel.Controls.Add($lblSearch)

    $txtSearch = New-Object System.Windows.Forms.TextBox
    $txtSearch.Location = New-Object System.Drawing.Point(65, 7)
    $txtSearch.Size = New-Object System.Drawing.Size(300, 20)
    $topPanel.Controls.Add($txtSearch)

    $lblCount = New-Object System.Windows.Forms.Label
    $lblCount.Location = New-Object System.Drawing.Point(380, 10)
    $lblCount.Size = New-Object System.Drawing.Size(290, 20)
    $lblCount.ForeColor = [System.Drawing.Color]::Gray
    $topPanel.Controls.Add($lblCount)

    $grid = New-Object System.Windows.Forms.DataGridView
    $grid.Dock = "Fill"
    $grid.ReadOnly = $true
    $grid.AllowUserToAddRows = $false
    $grid.MultiSelect = $false
    $grid.SelectionMode = "FullRowSelect"
    $grid.AutoSizeColumnsMode = "Fill"

    $loadCandidates = {
        $term = $txtSearch.Text.Trim().ToLowerInvariant()
        $rows = @($script:Items.Rows | Where-Object {
            [string]::IsNullOrWhiteSpace("$($_.Description)") -and [int]$_.Id -ne $ExcludeId
        })
        if (-not [string]::IsNullOrWhiteSpace($term)) {
            $rows = @($rows | Where-Object {
                "$($_.DeviceName)".ToLowerInvariant().Contains($term) -or
                "$($_.Model)".ToLowerInvariant().Contains($term) -or
                "$($_.SerialNumber)".ToLowerInvariant().Contains($term)
            })
        }
        $display = @($rows | ForEach-Object {
            $row = $_
            $statusLabel = ($script:Statuses | Where-Object { $_.Key -eq $row.Status }).Label
            [PSCustomObject]@{ Id = $row.Id; DeviceName = $row.DeviceName; Tab = $statusLabel; Model = $row.Model; SerialNumber = $row.SerialNumber }
        } | Sort-Object DeviceName)
        $grid.DataSource = [System.Collections.ArrayList]$display
        if ($grid.Columns["Id"]) { $grid.Columns["Id"].Visible = $false }
        $lblCount.Text = "$($display.Count) available device(s)"
    }
    $txtSearch.add_TextChanged({ & $loadCandidates })

    $bottomPanel = New-Object System.Windows.Forms.Panel
    $bottomPanel.Dock = "Bottom"
    $bottomPanel.Height = 45

    $btnSelect = New-Object System.Windows.Forms.Button
    $btnSelect.Text = "Select"
    $btnSelect.Location = New-Object System.Drawing.Point(510, 8)
    $btnSelect.Size = New-Object System.Drawing.Size(80, 28)
    $btnSelect.DialogResult = [System.Windows.Forms.DialogResult]::OK
    $bottomPanel.Controls.Add($btnSelect)

    $btnCancel = New-Object System.Windows.Forms.Button
    $btnCancel.Text = "Cancel"
    $btnCancel.Location = New-Object System.Drawing.Point(600, 8)
    $btnCancel.Size = New-Object System.Drawing.Size(80, 28)
    $btnCancel.DialogResult = [System.Windows.Forms.DialogResult]::Cancel
    $bottomPanel.Controls.Add($btnCancel)

    $dlg.Controls.Add($grid)
    $dlg.Controls.Add($bottomPanel)
    $dlg.Controls.Add($topPanel)
    $dlg.AcceptButton = $btnSelect
    $dlg.CancelButton = $btnCancel

    & $loadCandidates

    $result = $null
    if ($dlg.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK -and $grid.SelectedRows.Count -eq 1) {
        $selectedId = [int]$grid.SelectedRows[0].Cells["Id"].Value
        $result = $script:Items.Rows.Find($selectedId)
    }
    $dlg.Dispose()
    return $result
}

function Invoke-ComputerIdentityHandoff($OldRow, $NewRow, [string]$FirstName, [string]$LastName, [string]$Username, [string]$OldAdFlagReason) {
    # Shared by both "Transfer User to New Computer" and the general "Transfer User" action so
    # the actual AD-write/local-field logic for moving a person's identity between two devices
    # lives in exactly one place.
    $oldDeviceName = "$($OldRow.DeviceName)".Trim()
    $newDeviceName = "$($NewRow.DeviceName)".Trim()
    $fullName = "$FirstName $LastName".Trim()

    # Old device: a real AD write (disables the actual computer object, not just the inventory
    # record), Description cleared so it doesn't keep showing a name that no longer uses it, and
    # the local user assignment is cleared so it doesn't look like two people share this device.
    Set-AdComputerDisabled -DeviceName $oldDeviceName
    Sync-AdComputerDescription -DeviceName $oldDeviceName -Description ""
    $OldRow["Description"] = ""
    $OldRow["Username"] = ""
    $OldRow["FirstName"] = ""
    $OldRow["LastName"] = ""
    $OldRow["ADFlagReason"] = ""
    $OldRow["ADComputerDisabled"] = "Computer object disabled in AD"
    $OldRow["Status"] = "DisabledDevices"

    # New device: takes over the user's identity and known AD standing - not the old device's
    # hardware fields (Model/Serial/etc. stay whatever this device actually is).
    $newDescription = "$fullName***"
    Sync-AdComputerDescription -DeviceName $newDeviceName -Description $newDescription
    $NewRow["Username"] = $Username
    $NewRow["FirstName"] = $FirstName
    $NewRow["LastName"] = $LastName
    $NewRow["Description"] = $newDescription
    $NewRow["ADFlagReason"] = $OldAdFlagReason
    $NewRow["ADComputerDisabled"] = ""
    $NewRow["Status"] = "Current"
}

function Show-TransferUserDialog([string]$FullName, $CurrentDeviceRow, [string]$CurrentManagerDisplay, [string]$CurrentOuDn, [string]$CurrentDescription, [string]$CurrentOffice, [bool]$HasAdUser = $true) {
    # General-purpose "what's changing for this user" dialog - Manager, OU, and Computer are
    # each independently optional (a "No change" checkbox per field), but every field starts
    # UNCHECKED so the admin has to make an explicit choice (pick a new value, or tick "No
    # change") for each one rather than something being silently left alone by default.
    # Description/Office are plain AD text attributes (ADUC's General tab) - no picker needed,
    # so unlike Manager/OU/Computer they're just ordinary editable textboxes pre-filled with the
    # current value; a "change" is detected by comparing the final text back against what was
    # there when the dialog opened, same as editing them directly in AD.
    $currentDeviceName = "$($CurrentDeviceRow.DeviceName)".Trim()
    $currentDeviceId = [int]$CurrentDeviceRow["Id"]

    $dlg = New-Object System.Windows.Forms.Form
    $dlg.Text = "Transfer User: $FullName"
    if ($script:AppIcon) { $dlg.Icon = $script:AppIcon }
    $dlg.Size = New-Object System.Drawing.Size(640, 500)
    $dlg.StartPosition = "CenterParent"
    $dlg.MinimumSize = New-Object System.Drawing.Size(560, 460)

    $lblTicket = New-Object System.Windows.Forms.Label
    $lblTicket.Text = "Ticket #:"
    $lblTicket.Location = New-Object System.Drawing.Point(20, 20)
    $lblTicket.Size = New-Object System.Drawing.Size(120, 20)
    $txtTicket = New-Object System.Windows.Forms.TextBox
    $txtTicket.Location = New-Object System.Drawing.Point(150, 17)
    $txtTicket.Size = New-Object System.Drawing.Size(200, 20)

    # ---- Manager ----
    $lblManager = New-Object System.Windows.Forms.Label
    $lblManager.Text = "Manager:"
    $lblManager.Location = New-Object System.Drawing.Point(20, 70)
    $lblManager.Size = New-Object System.Drawing.Size(120, 20)
    $txtManagerDisplay = New-Object System.Windows.Forms.TextBox
    $txtManagerDisplay.Location = New-Object System.Drawing.Point(150, 67)
    $txtManagerDisplay.Size = New-Object System.Drawing.Size(340, 20)
    $txtManagerDisplay.ReadOnly = $true
    $txtManagerDisplay.Text = "(not yet selected - current: $CurrentManagerDisplay)"
    $btnBrowseManager = New-Object System.Windows.Forms.Button
    $btnBrowseManager.Text = "Browse..."
    $btnBrowseManager.Location = New-Object System.Drawing.Point(500, 65)
    $btnBrowseManager.Size = New-Object System.Drawing.Size(100, 26)
    $chkManagerNoChange = New-Object System.Windows.Forms.CheckBox
    $chkManagerNoChange.Text = "No change to Manager"
    $chkManagerNoChange.Location = New-Object System.Drawing.Point(150, 95)
    $chkManagerNoChange.Size = New-Object System.Drawing.Size(250, 20)
    # $state is a Hashtable (a reference type) shared by every handler below - handlers MUTATE
    # its entries ($state.ManagerDn = ...) rather than reassigning a bare local variable. Bare
    # reassignment inside a WinForms event handler scriptblock silently shadows a NEW local
    # instead of writing back to this function's variable (the same closure-scoping gotcha
    # already documented elsewhere in this file for the Onboarding Wizard) - mutating an object
    # already captured by reference sidesteps that entirely.
    $state = @{ ManagerDn = $null; ManagerDisplay = $null; OuDn = $null; ComputerRow = $null; Result = $null }
    $btnBrowseManager.add_Click({
        $picked = Show-ManagerPickerDialog
        if ($null -ne $picked) {
            $state.ManagerDn = $picked.DistinguishedName
            $state.ManagerDisplay = "$($picked.FirstName) $($picked.LastName) ($($picked.SamAccountName))"
            $txtManagerDisplay.Text = "New: $($state.ManagerDisplay)"
        }
    })
    $chkManagerNoChange.add_CheckedChanged({
        if ($chkManagerNoChange.Checked) {
            $btnBrowseManager.Enabled = $false
            $state.ManagerDn = $null
            $state.ManagerDisplay = $null
            $txtManagerDisplay.Text = "(no change - current: $CurrentManagerDisplay)"
        } else {
            $btnBrowseManager.Enabled = $true
            $txtManagerDisplay.Text = "(not yet selected - current: $CurrentManagerDisplay)"
        }
    })

    # ---- Organizational Unit ----
    $lblOu = New-Object System.Windows.Forms.Label
    $lblOu.Text = "Organizational Unit:"
    $lblOu.Location = New-Object System.Drawing.Point(20, 135)
    $lblOu.Size = New-Object System.Drawing.Size(120, 20)
    $txtOuDisplay = New-Object System.Windows.Forms.TextBox
    $txtOuDisplay.Location = New-Object System.Drawing.Point(150, 132)
    $txtOuDisplay.Size = New-Object System.Drawing.Size(340, 20)
    $txtOuDisplay.ReadOnly = $true
    $txtOuDisplay.Text = "(not yet selected - current: $CurrentOuDn)"
    $btnBrowseOu = New-Object System.Windows.Forms.Button
    $btnBrowseOu.Text = "Browse..."
    $btnBrowseOu.Location = New-Object System.Drawing.Point(500, 130)
    $btnBrowseOu.Size = New-Object System.Drawing.Size(100, 26)
    $chkOuNoChange = New-Object System.Windows.Forms.CheckBox
    $chkOuNoChange.Text = "No change to OU"
    $chkOuNoChange.Location = New-Object System.Drawing.Point(150, 160)
    $chkOuNoChange.Size = New-Object System.Drawing.Size(250, 20)
    $btnBrowseOu.add_Click({
        $picked = Show-OuPickerDialog
        if ($null -ne $picked) {
            $state.OuDn = $picked
            $txtOuDisplay.Text = "New: $($state.OuDn)"
        }
    })
    $chkOuNoChange.add_CheckedChanged({
        if ($chkOuNoChange.Checked) {
            $btnBrowseOu.Enabled = $false
            $state.OuDn = $null
            $txtOuDisplay.Text = "(no change - current: $CurrentOuDn)"
        } else {
            $btnBrowseOu.Enabled = $true
            $txtOuDisplay.Text = "(not yet selected - current: $CurrentOuDn)"
        }
    })

    # ---- Computer ----
    $lblComputer = New-Object System.Windows.Forms.Label
    $lblComputer.Text = "Computer:"
    $lblComputer.Location = New-Object System.Drawing.Point(20, 200)
    $lblComputer.Size = New-Object System.Drawing.Size(120, 20)
    $txtComputerDisplay = New-Object System.Windows.Forms.TextBox
    $txtComputerDisplay.Location = New-Object System.Drawing.Point(150, 197)
    $txtComputerDisplay.Size = New-Object System.Drawing.Size(340, 20)
    $txtComputerDisplay.ReadOnly = $true
    $txtComputerDisplay.Text = "(not yet selected - current: $currentDeviceName)"
    $btnBrowseComputer = New-Object System.Windows.Forms.Button
    $btnBrowseComputer.Text = "Browse..."
    $btnBrowseComputer.Location = New-Object System.Drawing.Point(500, 195)
    $btnBrowseComputer.Size = New-Object System.Drawing.Size(100, 26)
    $chkComputerNoChange = New-Object System.Windows.Forms.CheckBox
    $chkComputerNoChange.Text = "No change to Computer"
    $chkComputerNoChange.Location = New-Object System.Drawing.Point(150, 225)
    $chkComputerNoChange.Size = New-Object System.Drawing.Size(250, 20)
    $btnBrowseComputer.add_Click({
        $picked = Show-TransferTargetDialog -ExcludeId $currentDeviceId -Title "Select New Computer for '$FullName'"
        if ($null -ne $picked) {
            $state.ComputerRow = $picked
            $txtComputerDisplay.Text = "New: $($picked.DeviceName)"
        }
    })
    $chkComputerNoChange.add_CheckedChanged({
        if ($chkComputerNoChange.Checked) {
            $btnBrowseComputer.Enabled = $false
            $state.ComputerRow = $null
            $txtComputerDisplay.Text = "(no change - current: $currentDeviceName)"
        } else {
            $btnBrowseComputer.Enabled = $true
            $txtComputerDisplay.Text = "(not yet selected - current: $currentDeviceName)"
        }
    })

    # ---- Description / Office (plain text, no picker, no "No change" checkbox needed) ----
    $lblDescription = New-Object System.Windows.Forms.Label
    $lblDescription.Text = "Description:"
    $lblDescription.Location = New-Object System.Drawing.Point(20, 265)
    $lblDescription.Size = New-Object System.Drawing.Size(120, 20)
    $txtDescription = New-Object System.Windows.Forms.TextBox
    $txtDescription.Location = New-Object System.Drawing.Point(150, 262)
    $txtDescription.Size = New-Object System.Drawing.Size(450, 20)
    $txtDescription.Text = $CurrentDescription

    $lblOffice = New-Object System.Windows.Forms.Label
    $lblOffice.Text = "Office:"
    $lblOffice.Location = New-Object System.Drawing.Point(20, 300)
    $lblOffice.Size = New-Object System.Drawing.Size(120, 20)
    $txtOffice = New-Object System.Windows.Forms.TextBox
    $txtOffice.Location = New-Object System.Drawing.Point(150, 297)
    $txtOffice.Size = New-Object System.Drawing.Size(450, 20)
    $txtOffice.Text = $CurrentOffice

    # No linked AD account for this row (see Invoke-TransferUser's $hasAdUser) - Manager, OU,
    # Description, and Office are all real AD-user writes and there's no account to write to, so
    # those sections are locked to "No change"/disabled and only Computer stays editable. This is
    # the one thing the old computer-only Transfer used to handle that this general dialog
    # otherwise wouldn't, now folded in here instead of losing the capability.
    if (-not $HasAdUser) {
        $chkManagerNoChange.Checked = $true
        $chkManagerNoChange.Enabled = $false
        $btnBrowseManager.Enabled = $false
        $chkOuNoChange.Checked = $true
        $chkOuNoChange.Enabled = $false
        $btnBrowseOu.Enabled = $false
        $txtDescription.Enabled = $false
        $txtOffice.Enabled = $false
    }

    $lblHint = New-Object System.Windows.Forms.Label
    $lblHint.Text = if ($HasAdUser) {
        "For Manager/OU/Computer: either select a new value via Browse, or check 'No change'."
    } else {
        "No linked AD username on this device - only Computer can be transferred. Use Match User first to also edit Manager/OU/Description/Office."
    }
    $lblHint.ForeColor = [System.Drawing.Color]::DimGray
    $lblHint.Location = New-Object System.Drawing.Point(20, 340)
    $lblHint.Size = New-Object System.Drawing.Size(580, 20)

    $bottomPanel = New-Object System.Windows.Forms.Panel
    $bottomPanel.Dock = "Bottom"
    $bottomPanel.Height = 50
    $btnOk = New-Object System.Windows.Forms.Button
    $btnOk.Text = "Transfer"
    $btnOk.Location = New-Object System.Drawing.Point(440, 10)
    $btnOk.Size = New-Object System.Drawing.Size(90, 30)
    $bottomPanel.Controls.Add($btnOk)
    $btnCancel = New-Object System.Windows.Forms.Button
    $btnCancel.Text = "Cancel"
    $btnCancel.Location = New-Object System.Drawing.Point(535, 10)
    $btnCancel.Size = New-Object System.Drawing.Size(90, 30)
    $btnCancel.DialogResult = [System.Windows.Forms.DialogResult]::Cancel
    $bottomPanel.Controls.Add($btnCancel)

    $dlg.Controls.AddRange(@($lblTicket, $txtTicket, $lblManager, $txtManagerDisplay, $btnBrowseManager, $chkManagerNoChange,
        $lblOu, $txtOuDisplay, $btnBrowseOu, $chkOuNoChange, $lblComputer, $txtComputerDisplay, $btnBrowseComputer, $chkComputerNoChange,
        $lblDescription, $txtDescription, $lblOffice, $txtOffice,
        $lblHint, $bottomPanel))
    $dlg.CancelButton = $btnCancel

    $btnOk.add_Click({
        if ([string]::IsNullOrWhiteSpace($txtTicket.Text)) {
            [System.Windows.Forms.MessageBox]::Show("Enter a ticket number.", "Transfer User", "OK", "Warning") | Out-Null
            return
        }
        if (-not $chkManagerNoChange.Checked -and $null -eq $state.ManagerDn) {
            [System.Windows.Forms.MessageBox]::Show("Select a new Manager, or check 'No change to Manager'.", "Transfer User", "OK", "Warning") | Out-Null
            return
        }
        if (-not $chkOuNoChange.Checked -and $null -eq $state.OuDn) {
            [System.Windows.Forms.MessageBox]::Show("Select a new Organizational Unit, or check 'No change to OU'.", "Transfer User", "OK", "Warning") | Out-Null
            return
        }
        if (-not $chkComputerNoChange.Checked -and $null -eq $state.ComputerRow) {
            [System.Windows.Forms.MessageBox]::Show("Select a new Computer, or check 'No change to Computer'.", "Transfer User", "OK", "Warning") | Out-Null
            return
        }
        $descriptionChanged = ($txtDescription.Text.Trim() -ne "$CurrentDescription".Trim())
        $officeChanged = ($txtOffice.Text.Trim() -ne "$CurrentOffice".Trim())
        if ($chkManagerNoChange.Checked -and $chkOuNoChange.Checked -and $chkComputerNoChange.Checked -and -not $descriptionChanged -and -not $officeChanged) {
            [System.Windows.Forms.MessageBox]::Show("Nothing was selected to change - select at least one new value, edit Description/Office, or Cancel.", "Transfer User", "OK", "Warning") | Out-Null
            return
        }
        $state.Result = [PSCustomObject]@{
            TicketNumber      = $txtTicket.Text.Trim()
            ManagerChanged    = (-not $chkManagerNoChange.Checked)
            NewManagerDn      = $state.ManagerDn
            NewManagerDisplay = $state.ManagerDisplay
            OuChanged         = (-not $chkOuNoChange.Checked)
            NewOuDn           = $state.OuDn
            ComputerChanged   = (-not $chkComputerNoChange.Checked)
            NewComputerRow    = $state.ComputerRow
            DescriptionChanged = $descriptionChanged
            NewDescription     = $txtDescription.Text.Trim()
            OfficeChanged       = $officeChanged
            NewOffice           = $txtOffice.Text.Trim()
        }
        $dlg.DialogResult = [System.Windows.Forms.DialogResult]::OK
        $dlg.Close()
    })

    [void]$dlg.ShowDialog()
    $dlg.Dispose()
    return $state.Result
}

function Invoke-TransferUser([string]$StatusKey) {
    $grid = $script:Grids[$StatusKey]
    if ($grid.SelectedRows.Count -ne 1) {
        [System.Windows.Forms.MessageBox]::Show("Select exactly one device to transfer its user.", "Transfer User", "OK", "Information") | Out-Null
        return
    }
    if (-not (Test-AdModuleAvailable)) { return }

    $rowId = [int]$grid.SelectedRows[0].DataBoundItem["Id"]
    $row = $script:Items.Rows.Find($rowId)
    if ($null -eq $row) { return }

    $deviceName = "$($row.DeviceName)".Trim()
    $username = "$($row.Username)".Trim()
    $firstName = "$($row.FirstName)".Trim()
    $lastName = "$($row.LastName)".Trim()
    $fullName = "$firstName $lastName".Trim()

    if ([string]::IsNullOrWhiteSpace($firstName) -and [string]::IsNullOrWhiteSpace($lastName) -and [string]::IsNullOrWhiteSpace($username)) {
        [System.Windows.Forms.MessageBox]::Show("'$deviceName' has no assigned user to transfer.", "Transfer User", "OK", "Information") | Out-Null
        return
    }

    $adParams = Get-AdCredParams
    # No linked AD username (row was only ever entered as First/Last, never matched to a real
    # account) - Manager/OU/Description/Office are real AD-user writes and need a resolvable
    # account, so those sections stay disabled in the dialog below and only the Computer can be
    # transferred. This is the one case the old computer-only Transfer used to handle on its
    # own; folding it in here (rather than requiring a linked username outright) keeps that
    # capability instead of losing it when the two Transfer actions were consolidated into one.
    $hasAdUser = -not [string]::IsNullOrWhiteSpace($username)
    $currentManagerDisplay = "(none)"
    $currentOuDn = ""
    $currentDescription = ""
    $currentOffice = ""
    $adUser = $null
    if ($hasAdUser) {
        try {
            $adUser = Get-ADUser @adParams -Identity $username -Properties Manager, DistinguishedName, Description, Office -ErrorAction Stop
        } catch {
            [System.Windows.Forms.MessageBox]::Show("Could not look up '$username' in Active Directory: $($_.Exception.Message)", "Active Directory Error", "OK", "Error") | Out-Null
            return
        }
        if (-not [string]::IsNullOrWhiteSpace("$($adUser.Manager)")) {
            try {
                $mgr = Get-ADUser @adParams -Identity "$($adUser.Manager)" -Properties GivenName, Surname, SamAccountName -ErrorAction Stop
                $currentManagerDisplay = "$($mgr.GivenName) $($mgr.Surname) ($($mgr.SamAccountName))".Trim()
            } catch {
                $currentManagerDisplay = "$($adUser.Manager)"
            }
        }
        # "everything after the CN component" - AD escapes a literal comma inside the CN itself
        # as "\," (confirmed live: a "Last\, First" display name produces exactly that), so a
        # naive split on the first comma lands in the middle of the name instead of at the real
        # OU boundary. (?:\\.|[^,])* skips over any backslash-escaped character (comma included)
        # before matching the real, unescaped separator.
        $currentOuDn = if ("$($adUser.DistinguishedName)" -match '^CN=(?:\\.|[^,])*,(.+)$') { $matches[1] } else { "$($adUser.DistinguishedName)" }
        $currentDescription = "$($adUser.Description)"
        $currentOffice = "$($adUser.Office)"
    }

    $result = Show-TransferUserDialog -FullName $fullName -CurrentDeviceRow $row -CurrentManagerDisplay $currentManagerDisplay -CurrentOuDn $currentOuDn -CurrentDescription $currentDescription -CurrentOffice $currentOffice -HasAdUser $hasAdUser
    if ($null -eq $result) { return }

    $changes = New-Object System.Collections.Generic.List[string]
    if ($result.ManagerChanged) { $changes.Add("Manager: $currentManagerDisplay -> $($result.NewManagerDisplay)") }
    if ($result.OuChanged) { $changes.Add("OU: $currentOuDn -> $($result.NewOuDn)") }
    if ($result.ComputerChanged) { $changes.Add("Computer: $deviceName -> $($result.NewComputerRow.DeviceName)") }
    if ($result.DescriptionChanged) { $changes.Add("Description: '$currentDescription' -> '$($result.NewDescription)'") }
    if ($result.OfficeChanged) { $changes.Add("Office: '$currentOffice' -> '$($result.NewOffice)'") }
    $changesText = $changes -join "`r`n"
    $changesTextSemi = $changes -join "; "

    $confirmMsg = "Transfer $fullName (ticket # $($result.TicketNumber))?`r`n`r`n$changesText"
    $confirm = [System.Windows.Forms.MessageBox]::Show($confirmMsg, "Confirm Transfer", "YesNo", "Question")
    if ($confirm -ne [System.Windows.Forms.DialogResult]::Yes) { return }

    $anyAdFailure = $false

    if ($result.ManagerChanged) {
        try {
            Set-ADUser @adParams -Identity $username -Manager $result.NewManagerDn -ErrorAction Stop
            Write-AppLog "Transfer User: '$username' manager changed to '$($result.NewManagerDisplay)'." "Info"
        } catch {
            Write-AppLog "Transfer User: could NOT change '$username''s manager: $($_.Exception.Message)" "Error"
            $anyAdFailure = $true
        }
    }

    if ($result.OuChanged) {
        if (-not (Test-OuIsAllowedForNewUsers $result.NewOuDn)) {
            Write-AppLog "Transfer User: refused to move '$username' to '$($result.NewOuDn)' - not an allowed OU." "Error"
            $anyAdFailure = $true
        } else {
            try {
                Move-ADObject @adParams -Identity "$($adUser.DistinguishedName)" -TargetPath $result.NewOuDn -ErrorAction Stop
                Write-AppLog "Transfer User: '$username' moved to OU '$($result.NewOuDn)'." "Info"
            } catch {
                Write-AppLog "Transfer User: could NOT move '$username' to '$($result.NewOuDn)': $($_.Exception.Message)" "Error"
                $anyAdFailure = $true
            }
        }
    }

    if ($result.ComputerChanged) {
        $oldAdFlagReason = "$($row.ADFlagReason)"
        Invoke-ComputerIdentityHandoff -OldRow $row -NewRow $result.NewComputerRow -FirstName $firstName -LastName $lastName -Username $username -OldAdFlagReason $oldAdFlagReason
        Save-Inventory
        Refresh-AllTabs
    }

    if ($result.DescriptionChanged) {
        try {
            # -Description "" does not reliably clear the attribute (same AD behavior already
            # confirmed for computer objects elsewhere in this app) - -Clear is the correct way
            # to remove a single-valued attribute's value entirely.
            if ([string]::IsNullOrEmpty($result.NewDescription)) {
                Set-ADUser @adParams -Identity $username -Clear Description -ErrorAction Stop
            } else {
                Set-ADUser @adParams -Identity $username -Description $result.NewDescription -ErrorAction Stop
            }
            Write-AppLog "Transfer User: '$username' Description changed to '$($result.NewDescription)'." "Info"
        } catch {
            Write-AppLog "Transfer User: could NOT change '$username''s Description: $($_.Exception.Message)" "Error"
            $anyAdFailure = $true
        }
    }

    if ($result.OfficeChanged) {
        try {
            if ([string]::IsNullOrEmpty($result.NewOffice)) {
                Set-ADUser @adParams -Identity $username -Clear Office -ErrorAction Stop
            } else {
                Set-ADUser @adParams -Identity $username -Office $result.NewOffice -ErrorAction Stop
            }
            Write-AppLog "Transfer User: '$username' Office changed to '$($result.NewOffice)'." "Info"
        } catch {
            Write-AppLog "Transfer User: could NOT change '$username''s Office: $($_.Exception.Message)" "Error"
            $anyAdFailure = $true
        }
    }

    # No linked AD account (see $hasAdUser above) means there's no user object to stamp Notes
    # onto - the change still stays on record via the activity log below either way.
    if ($hasAdUser) {
        $actorDisplay = Get-AdActorDisplayName -AdParams $adParams
        try {
            Add-AdUserNoteLine -SamAccountName $username -ActionText "Transferred by $actorDisplay - Ticket # $($result.TicketNumber). Changed: $changesTextSemi" -AdParams $adParams
        } catch {
            Write-AppLog "Transfer for '$username' completed, but the user's AD Notes could NOT be updated: $($_.Exception.Message)" "Error"
            $anyAdFailure = $true
        }
    }

    Write-AppLog "Transfer User complete for '$fullName' (ticket # $($result.TicketNumber)). Changed: $changesTextSemi" "Info"
    $failureNote = ""
    if ($anyAdFailure) { $failureNote = "`r`n`r`nSome AD changes failed - check the activity log." }
    $doneMsg = "Transfer complete for $fullName.`r`n`r`n$changesText$failureNote"
    [System.Windows.Forms.MessageBox]::Show($doneMsg, "Transfer Complete", "OK", "Information") | Out-Null
}

function Invoke-OffboardUser([string]$StatusKey) {
    # Launches the standalone Offboarding2026.ps1 tool (pre-filling its AD search, never its
    # actions or confirmations - the operator drives everything inside it, same as running it by
    # hand). This app only acts afterward, and only when the tool's own result file confirms a
    # real, error-free run for the SAME user this row is about - anything else (window closed
    # without running, test mode, a different user, errors) leaves the computer and inventory
    # record untouched and just logs why, so a partial/uncertain offboarding never silently
    # cascades into disabling a computer that's still actively assigned.
    $grid = $script:Grids[$StatusKey]
    if ($grid.SelectedRows.Count -ne 1) {
        [System.Windows.Forms.MessageBox]::Show("Select exactly one device to offboard its user.", "Offboard User", "OK", "Information") | Out-Null
        return
    }
    if (-not (Test-Path $script:OffboardingScriptPath)) {
        [System.Windows.Forms.MessageBox]::Show("Offboarding script not found at:`n$($script:OffboardingScriptPath)", "Offboard User", "OK", "Error") | Out-Null
        return
    }

    $rowId = [int]$grid.SelectedRows[0].DataBoundItem["Id"]
    $row = $script:Items.Rows.Find($rowId)
    if ($null -eq $row) { return }

    $deviceName = "$($row.DeviceName)".Trim()
    $lastName = "$($row.LastName)".Trim()
    $firstName = "$($row.FirstName)".Trim()
    $username = "$($row.Username)".Trim()
    $fullName = "$firstName $lastName".Trim()
    if ([string]::IsNullOrWhiteSpace($lastName) -and [string]::IsNullOrWhiteSpace($firstName) -and [string]::IsNullOrWhiteSpace($username)) {
        [System.Windows.Forms.MessageBox]::Show("'$deviceName' has no assigned user to offboard.", "Offboard User", "OK", "Information") | Out-Null
        return
    }

    # The recorded AD Username (SamAccountName) is an exact match for the offboarding tool's
    # search; fall back to the full name if AD sync never recorded one for this row.
    $prefillTerm = if (-not [string]::IsNullOrWhiteSpace($username)) { $username } else { $fullName }
    $resultFile = Join-Path ([System.IO.Path]::GetTempPath()) "CONTOSOOffboard_$([guid]::NewGuid().ToString('N')).json"

    # Hand off the SU login's own credential so offboarding's AD actions run as that account,
    # not whoever is logged into Windows - Export-Clixml encrypts the password with DPAPI keyed
    # to this Windows user+machine, so the file is unreadable to anyone else even briefly on
    # disk; the offboarding script deletes it the moment it's loaded.
    $credFile = $null
    if ($script:AdCredential) {
        $credFile = Join-Path ([System.IO.Path]::GetTempPath()) "CONTOSOOffboardCred_$([guid]::NewGuid().ToString('N')).xml"
        try {
            $script:AdCredential | Export-Clixml -Path $credFile
        } catch {
            Write-AppLog "Could not prepare the AD credential handoff for offboarding: $($_.Exception.Message). It will run as the current Windows session instead." "Warning"
            $credFile = $null
        }
    }

    Write-AppLog "Launching offboarding tool for '$fullName' (device '$deviceName')..." "Info"
    $argList = @(
        "-NoProfile", "-ExecutionPolicy", "Bypass",
        "-File", "`"$($script:OffboardingScriptPath)`"",
        "-PrefillSearch", "`"$prefillTerm`"",
        "-ResultFile", "`"$resultFile`""
    )
    if ($credFile) { $argList += @("-CredentialFile", "`"$credFile`"") }
    try {
        $proc = Start-Process -FilePath "powershell.exe" -ArgumentList $argList -PassThru -ErrorAction Stop
    } catch {
        [System.Windows.Forms.MessageBox]::Show("Could not launch the offboarding tool: $($_.Exception.Message)", "Offboard User", "OK", "Error") | Out-Null
        if ($credFile) { Remove-Item -Path $credFile -Force -ErrorAction SilentlyContinue }
        return
    }

    # Pump this app's own message queue while waiting, instead of a blocking -Wait, so it stays
    # responsive (doesn't show "Not Responding") for however long the operator is in the
    # offboarding tool.
    while (-not $proc.HasExited) {
        [System.Windows.Forms.Application]::DoEvents()
        Start-Sleep -Milliseconds 200
    }

    # Defense in depth: the offboarding script deletes this itself right after loading it, but
    # clean up here too in case it crashed or was killed before reaching that point.
    if ($credFile -and (Test-Path $credFile)) { Remove-Item -Path $credFile -Force -ErrorAction SilentlyContinue }

    $result = $null
    if (Test-Path $resultFile) {
        try { $result = Get-Content $resultFile -Raw | ConvertFrom-Json } catch { }
        Remove-Item $resultFile -ErrorAction SilentlyContinue
    }

    if ($null -eq $result) {
        Write-AppLog "Offboarding tool closed for '$fullName' - no completed run was detected (window closed without running, or an older copy of the script). No computer or inventory changes were made." "Warning"
        return
    }
    if ($result.TestMode) {
        Write-AppLog "Offboarding tool ran for '$($result.SamAccountName)' in TEST MODE - no real AD changes were made, so '$deviceName' and its inventory record were left alone." "Warning"
        return
    }
    if (-not [string]::IsNullOrWhiteSpace($username) -and "$($result.SamAccountName)".Trim().ToLowerInvariant() -ne $username.ToLowerInvariant()) {
        Write-AppLog "Offboarding tool ran for '$($result.SamAccountName)', not the expected '$username' - skipping the automatic computer/inventory update for '$deviceName'. Handle it manually if this was intentional." "Warning"
        return
    }
    if ($result.ErrorCount -gt 0) {
        Write-AppLog "Offboarding for '$($result.SamAccountName)' completed with $($result.ErrorCount) error(s) - review its log before disabling/moving '$deviceName'. Skipping the automatic step." "Warning"
        return
    }

    $confirm = [System.Windows.Forms.MessageBox]::Show(
        "Offboarding completed successfully for $($result.SamAccountName) (ticket: $($result.Ticket)), no errors.`n`nDisable AD computer '$deviceName' and mark this device Terminated in inventory now?",
        "Finish Offboarding", "YesNo", "Question")
    if ($confirm -ne [System.Windows.Forms.DialogResult]::Yes) {
        Write-AppLog "Offboarding for '$($result.SamAccountName)' confirmed complete, but the operator declined the automatic computer/inventory update for '$deviceName'." "Info"
        return
    }

    # Reuses whatever ticket # was already typed into the offboarding tool as a starting point -
    # the operator can still change it here, since the computer disable might reasonably be
    # tracked under a different ticket than the user offboarding itself. Required - a ticket
    # number is always on file for a change this significant.
    $ticketNumber = Show-TicketNumberDialog -DefaultValue $result.Ticket -PromptText "Enter the ticket number for this offboarding - it will be stamped into the computer's AD Description and appended to the user's AD Notes."
    if ([string]::IsNullOrWhiteSpace($ticketNumber)) {
        Write-AppLog "Offboarding for '$($result.SamAccountName)' confirmed complete, but no ticket number was entered - the automatic computer/inventory update for '$deviceName' was skipped." "Info"
        return
    }

    $adParams = Get-AdCredParams
    $disabledDescription = "Disabled per ticket # $ticketNumber"
    Set-AdComputerDisabled -DeviceName $deviceName
    Sync-AdComputerDescription -DeviceName $deviceName -Description $disabledDescription
    try {
        Add-AdUserNoteLine -SamAccountName $result.SamAccountName -ActionText "Offboarded by $(Get-AdActorDisplayName -AdParams $adParams) - Ticket # $ticketNumber" -AdParams $adParams
    } catch {
        Write-AppLog "'$deviceName' was disabled, but the user's AD Notes could NOT be updated: $($_.Exception.Message)" "Error"
    }
    $row["ADComputerDisabled"] = "Computer object disabled in AD"
    $row["ADFlagReason"] = ""
    $row["Status"] = "Terminated"
    # Clears everything tied to the PERSON who was assigned this device - not the device's own
    # hardware/config fields (Model, Serial #, Domain, ESET, etc.), which stay exactly as they
    # were, matching the explicit "leave everything else" instruction. Description is set to the
    # ticket stamp rather than cleared, so the reason for the disable stays visible in both AD
    # and inventory.
    $row["Description"] = $disabledDescription
    $row["Username"] = ""
    $row["FirstName"] = ""
    $row["LastName"] = ""
    $row["CellPhone"] = ""
    $row["DeviceModel"] = ""
    Save-Inventory
    Refresh-AllTabs
    Write-AppLog "Offboarding finished: '$deviceName' disabled (ticket #$ticketNumber), assigned-user fields cleared, and marked Terminated." "Info"
    [System.Windows.Forms.MessageBox]::Show("'$deviceName' has been disabled and marked Terminated.", "Offboarding Complete", "OK", "Information") | Out-Null
}

function Test-OuIsAllowedForNewUsers([string]$Dn) {
    # Must be strictly WITHIN $script:AllowedUserOuBaseDn - a sub-OU at any depth - never the
    # base OU itself. String comparison is safe here because DNs from AD are always returned in
    # the same canonical comma-separated form.
    if ([string]::IsNullOrWhiteSpace($Dn)) { return $false }
    $dnLower = $Dn.Trim().ToLowerInvariant()
    $baseLower = $script:AllowedUserOuBaseDn.ToLowerInvariant()
    return ($dnLower -ne $baseLower) -and $dnLower.EndsWith(",$baseLower")
}

function Get-PrivilegedGroupSids([hashtable]$AdParams) {
    $sids = New-Object System.Collections.Generic.HashSet[string]
    foreach ($rid in $script:PrivilegedBuiltinRids) { [void]$sids.Add("S-1-5-32-$rid") }
    try {
        $domainSid = (Get-ADDomain @AdParams -ErrorAction Stop).DomainSID.Value
        foreach ($rid in $script:PrivilegedGroupRids) { [void]$sids.Add("$domainSid-$rid") }
    } catch { }
    return $sids
}

function Test-GroupIsPrivileged([string]$GroupName, [hashtable]$AdParams) {
    # Name check first (cheap, catches the RID-less groups like DnsAdmins); SID check second
    # (catches every well-known-RID group even if it's been renamed in AD).
    if ($script:PrivilegedGroupNames -contains $GroupName) { return $true }
    try {
        $grp = Get-ADGroup @AdParams -Identity $GroupName -Properties SID -ErrorAction Stop
        $sidStr = if ($grp.SID) { $grp.SID.Value } else { "" }
        return (Get-PrivilegedGroupSids -AdParams $AdParams).Contains($sidStr)
    } catch {
        # Can't resolve the group at all - fail closed only where this is used for a real AD
        # write (the onboarding Finish step); the picker below just won't list it either way.
        return $false
    }
}

function Show-OuPickerDialog {
    # No fixed section->OU mapping exists, so this is a plain searchable pick-list (matching
    # Show-TransferTargetDialog's shape) rather than a navigable tree - simplest thing that
    # works given every remaining OU (after the allowed-scope filter below) is a legitimate
    # option here.
    if (-not (Test-AdModuleAvailable)) { return $null }
    $adParams = Get-AdCredParams
    try {
        $allOus = @(Get-ADOrganizationalUnit @adParams -Filter * -Properties Name, DistinguishedName | Sort-Object DistinguishedName)
    } catch {
        [System.Windows.Forms.MessageBox]::Show("Could not query Active Directory: $($_.Exception.Message)", "Active Directory Error", "OK", "Error") | Out-Null
        return $null
    }
    # Restricted to sub-OUs of $script:AllowedUserOuBaseDn only - not the base OU itself
    # (confirmed: new users may never be created directly in "Domain Users", only in one of its
    # children). This is onboarding's only caller of this dialog, so scoping it here is safe.
    $allOus = @($allOus | Where-Object { Test-OuIsAllowedForNewUsers "$($_.DistinguishedName)" })
    if ($allOus.Count -eq 0) {
        [System.Windows.Forms.MessageBox]::Show("No selectable Organizational Units were found under '$($script:AllowedUserOuBaseDn)'. Check that this base OU exists and has at least one sub-OU.", "No Organizational Units Found", "OK", "Warning") | Out-Null
        return $null
    }

    $dlg = New-Object System.Windows.Forms.Form
    $dlg.Text = "Select Organizational Unit (under Domain Users)"
    $dlg.Size = New-Object System.Drawing.Size(750, 450)
    $dlg.StartPosition = "CenterParent"
    $dlg.MinimumSize = New-Object System.Drawing.Size(500, 300)

    $topPanel = New-Object System.Windows.Forms.Panel
    $topPanel.Dock = "Top"
    $topPanel.Height = 36
    $lblSearch = New-Object System.Windows.Forms.Label
    $lblSearch.Text = "Search:"
    $lblSearch.Location = New-Object System.Drawing.Point(10, 10)
    $lblSearch.Size = New-Object System.Drawing.Size(50, 20)
    $topPanel.Controls.Add($lblSearch)
    $txtSearch = New-Object System.Windows.Forms.TextBox
    $txtSearch.Location = New-Object System.Drawing.Point(65, 7)
    $txtSearch.Size = New-Object System.Drawing.Size(400, 20)
    $topPanel.Controls.Add($txtSearch)

    $grid = New-Object System.Windows.Forms.DataGridView
    $grid.Dock = "Fill"
    $grid.ReadOnly = $true
    $grid.AllowUserToAddRows = $false
    $grid.MultiSelect = $false
    $grid.SelectionMode = "FullRowSelect"
    $grid.AutoSizeColumnsMode = "Fill"

    $displayAll = @($allOus | ForEach-Object { [PSCustomObject]@{ Name = $_.Name; DistinguishedName = $_.DistinguishedName } })
    $loadCandidates = {
        $term = $txtSearch.Text.Trim().ToLowerInvariant()
        $rows = if ([string]::IsNullOrWhiteSpace($term)) { $displayAll } else {
            @($displayAll | Where-Object { "$($_.DistinguishedName)".ToLowerInvariant().Contains($term) })
        }
        $grid.DataSource = [System.Collections.ArrayList]$rows
    }
    $txtSearch.add_TextChanged({ & $loadCandidates })

    $bottomPanel = New-Object System.Windows.Forms.Panel
    $bottomPanel.Dock = "Bottom"
    $bottomPanel.Height = 45
    $btnSelect = New-Object System.Windows.Forms.Button
    $btnSelect.Text = "Select"
    $btnSelect.Location = New-Object System.Drawing.Point(560, 8)
    $btnSelect.Size = New-Object System.Drawing.Size(80, 28)
    $btnSelect.DialogResult = [System.Windows.Forms.DialogResult]::OK
    $bottomPanel.Controls.Add($btnSelect)
    $btnCancel = New-Object System.Windows.Forms.Button
    $btnCancel.Text = "Cancel"
    $btnCancel.Location = New-Object System.Drawing.Point(650, 8)
    $btnCancel.Size = New-Object System.Drawing.Size(80, 28)
    $btnCancel.DialogResult = [System.Windows.Forms.DialogResult]::Cancel
    $bottomPanel.Controls.Add($btnCancel)

    $dlg.Controls.Add($grid)
    $dlg.Controls.Add($bottomPanel)
    $dlg.Controls.Add($topPanel)
    $dlg.AcceptButton = $btnSelect
    $dlg.CancelButton = $btnCancel

    & $loadCandidates

    $result = $null
    if ($dlg.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK -and $grid.SelectedRows.Count -eq 1) {
        $result = "$($grid.SelectedRows[0].Cells["DistinguishedName"].Value)"
    }
    $dlg.Dispose()
    return $result
}

function Show-GroupPickerDialog([array]$ExcludeNames = @()) {
    # Same shape as the OU picker, but multi-select (a new hire can land in several security
    # groups at once) and excludes group names already added on a previous pass through this
    # dialog, so re-opening it to add more doesn't re-list ones already picked.
    if (-not (Test-AdModuleAvailable)) { return @() }
    $adParams = Get-AdCredParams
    try {
        $allGroups = @(Get-ADGroup @adParams -Filter * -Properties Name, SID | Sort-Object Name)
    } catch {
        [System.Windows.Forms.MessageBox]::Show("Could not query Active Directory: $($_.Exception.Message)", "Active Directory Error", "OK", "Error") | Out-Null
        return @()
    }
    $excludeSet = New-Object System.Collections.Generic.HashSet[string]
    foreach ($n in $ExcludeNames) { [void]$excludeSet.Add("$n".ToLowerInvariant()) }
    # Privileged groups (Domain Admins, Enterprise Admins, DnsAdmins, etc. - see the deny-list
    # constants near the top of this file) are never offered here at all, so onboarding cannot
    # be used to grant domain-wide control even by mistake.
    $deniedSids = Get-PrivilegedGroupSids -AdParams $adParams
    $deniedNames = New-Object System.Collections.Generic.HashSet[string]
    foreach ($n in $script:PrivilegedGroupNames) { [void]$deniedNames.Add($n.ToLowerInvariant()) }
    $allGroups = @($allGroups | Where-Object {
        $sidStr = if ($_.SID) { $_.SID.Value } else { "" }
        (-not $excludeSet.Contains("$($_.Name)".ToLowerInvariant())) `
            -and (-not $deniedSids.Contains($sidStr)) `
            -and (-not $deniedNames.Contains("$($_.Name)".ToLowerInvariant()))
    })

    $dlg = New-Object System.Windows.Forms.Form
    $dlg.Text = "Add Security Groups"
    $dlg.Size = New-Object System.Drawing.Size(600, 450)
    $dlg.StartPosition = "CenterParent"
    $dlg.MinimumSize = New-Object System.Drawing.Size(400, 300)

    $topPanel = New-Object System.Windows.Forms.Panel
    $topPanel.Dock = "Top"
    $topPanel.Height = 36
    $lblSearch = New-Object System.Windows.Forms.Label
    $lblSearch.Text = "Search:"
    $lblSearch.Location = New-Object System.Drawing.Point(10, 10)
    $lblSearch.Size = New-Object System.Drawing.Size(50, 20)
    $topPanel.Controls.Add($lblSearch)
    $txtSearch = New-Object System.Windows.Forms.TextBox
    $txtSearch.Location = New-Object System.Drawing.Point(65, 7)
    $txtSearch.Size = New-Object System.Drawing.Size(300, 20)
    $topPanel.Controls.Add($txtSearch)

    $grid = New-Object System.Windows.Forms.DataGridView
    $grid.Dock = "Fill"
    $grid.ReadOnly = $true
    $grid.AllowUserToAddRows = $false
    $grid.MultiSelect = $true
    $grid.SelectionMode = "FullRowSelect"
    $grid.AutoSizeColumnsMode = "Fill"

    $displayAll = @($allGroups | ForEach-Object { [PSCustomObject]@{ Name = $_.Name } })
    $loadCandidates = {
        $term = $txtSearch.Text.Trim().ToLowerInvariant()
        $rows = if ([string]::IsNullOrWhiteSpace($term)) { $displayAll } else {
            @($displayAll | Where-Object { "$($_.Name)".ToLowerInvariant().Contains($term) })
        }
        $grid.DataSource = [System.Collections.ArrayList]$rows
    }
    $txtSearch.add_TextChanged({ & $loadCandidates })

    $bottomPanel = New-Object System.Windows.Forms.Panel
    $bottomPanel.Dock = "Bottom"
    $bottomPanel.Height = 45
    $btnSelect = New-Object System.Windows.Forms.Button
    $btnSelect.Text = "Add Selected"
    $btnSelect.Location = New-Object System.Drawing.Point(400, 8)
    $btnSelect.Size = New-Object System.Drawing.Size(90, 28)
    $btnSelect.DialogResult = [System.Windows.Forms.DialogResult]::OK
    $bottomPanel.Controls.Add($btnSelect)
    $btnCancel = New-Object System.Windows.Forms.Button
    $btnCancel.Text = "Cancel"
    $btnCancel.Location = New-Object System.Drawing.Point(500, 8)
    $btnCancel.Size = New-Object System.Drawing.Size(80, 28)
    $btnCancel.DialogResult = [System.Windows.Forms.DialogResult]::Cancel
    $bottomPanel.Controls.Add($btnCancel)

    $dlg.Controls.Add($grid)
    $dlg.Controls.Add($bottomPanel)
    $dlg.Controls.Add($topPanel)
    $dlg.AcceptButton = $btnSelect
    $dlg.CancelButton = $btnCancel

    & $loadCandidates

    $result = @()
    if ($dlg.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK -and $grid.SelectedRows.Count -gt 0) {
        $result = @($grid.SelectedRows | ForEach-Object { "$($_.Cells["Name"].Value)" })
    }
    $dlg.Dispose()
    return $result
}

function Show-OnboardingWizard {
    # A true multi-step wizard (Next/Back), the one UI pattern this app doesn't otherwise use -
    # everything else is a single dialog. Wizard progress/selection state that needs to be
    # MUTATED from inside a button's click handler is kept in $script: scope (a bare local
    # variable reassigned inside a nested {} scriptblock silently shadows the outer one instead
    # of updating it - confirmed by reproduction while building this - so $currentStep++ etc.
    # would otherwise just reset to 1 on every click and never actually advance). Anything only
    # ever READ by a handler (control references, the step panels themselves) stays a plain
    # local variable - only writes-from-a-handler need the $script: workaround.
    if (-not (Test-AdModuleAvailable)) { return }

    $script:OnboardWizStep = 0
    $script:OnboardWizOuDn = $null
    $script:OnboardWizManagerDn = $null
    $script:OnboardWizManagerDisplay = $null
    $script:OnboardWizComputerChecked = $false
    $script:OnboardWizComputerFound = $false
    $wizGroups = New-Object System.Collections.ArrayList

    $dlg = New-Object System.Windows.Forms.Form
    $dlg.Text = "Onboard New User"
    if ($script:AppIcon) { $dlg.Icon = $script:AppIcon }
    $dlg.Size = New-Object System.Drawing.Size(700, 520)
    $dlg.StartPosition = "CenterParent"
    $dlg.MinimumSize = New-Object System.Drawing.Size(600, 420)

    $lblStepTitle = New-Object System.Windows.Forms.Label
    $lblStepTitle.Dock = "Top"
    $lblStepTitle.Height = 32
    $lblStepTitle.Font = New-Object System.Drawing.Font($lblStepTitle.Font.FontFamily, 11, [System.Drawing.FontStyle]::Bold)
    $lblStepTitle.TextAlign = "MiddleLeft"
    $lblStepTitle.Padding = New-Object System.Windows.Forms.Padding(10, 0, 0, 0)

    $contentPanel = New-Object System.Windows.Forms.Panel
    $contentPanel.Dock = "Fill"

    # ---- Step 1: Basic Info ----
    $panel1 = New-Object System.Windows.Forms.Panel
    $panel1.Dock = "Fill"
    # Ticket # comes first, per explicit request - the admin logs which ticket this onboarding
    # is for before anything else, rather than it being buried later in the wizard.
    $lbl1t = New-Object System.Windows.Forms.Label
    $lbl1t.Text = "Ticket #:"
    $lbl1t.Location = New-Object System.Drawing.Point(30, 30)
    $lbl1t.Size = New-Object System.Drawing.Size(120, 20)
    $txtOnboardTicket = New-Object System.Windows.Forms.TextBox
    $txtOnboardTicket.Location = New-Object System.Drawing.Point(160, 27)
    $txtOnboardTicket.Size = New-Object System.Drawing.Size(200, 20)
    $lbl1a = New-Object System.Windows.Forms.Label
    $lbl1a.Text = "First Name:"
    $lbl1a.Location = New-Object System.Drawing.Point(30, 70)
    $lbl1a.Size = New-Object System.Drawing.Size(120, 20)
    $txtFirstName = New-Object System.Windows.Forms.TextBox
    $txtFirstName.Location = New-Object System.Drawing.Point(160, 67)
    $txtFirstName.Size = New-Object System.Drawing.Size(250, 20)
    $lbl1b = New-Object System.Windows.Forms.Label
    $lbl1b.Text = "Last Name:"
    $lbl1b.Location = New-Object System.Drawing.Point(30, 110)
    $lbl1b.Size = New-Object System.Drawing.Size(120, 20)
    $txtLastName = New-Object System.Windows.Forms.TextBox
    $txtLastName.Location = New-Object System.Drawing.Point(160, 107)
    $txtLastName.Size = New-Object System.Drawing.Size(250, 20)
    # Plain AD attributes (General tab in ADUC) - optional, freely editable, same as typing them
    # directly into a new user's properties.
    $lbl1d = New-Object System.Windows.Forms.Label
    $lbl1d.Text = "Description:"
    $lbl1d.Location = New-Object System.Drawing.Point(30, 150)
    $lbl1d.Size = New-Object System.Drawing.Size(120, 20)
    $txtOnboardDescription = New-Object System.Windows.Forms.TextBox
    $txtOnboardDescription.Location = New-Object System.Drawing.Point(160, 147)
    $txtOnboardDescription.Size = New-Object System.Drawing.Size(350, 20)
    $lbl1e = New-Object System.Windows.Forms.Label
    $lbl1e.Text = "Office:"
    $lbl1e.Location = New-Object System.Drawing.Point(30, 190)
    $lbl1e.Size = New-Object System.Drawing.Size(120, 20)
    $txtOnboardOffice = New-Object System.Windows.Forms.TextBox
    $txtOnboardOffice.Location = New-Object System.Drawing.Point(160, 187)
    $txtOnboardOffice.Size = New-Object System.Drawing.Size(350, 20)
    $panel1.Controls.AddRange(@($lbl1t, $txtOnboardTicket, $lbl1a, $txtFirstName, $lbl1b, $txtLastName, $lbl1d, $txtOnboardDescription, $lbl1e, $txtOnboardOffice))

    # ---- Step 2: Account Details ----
    $panel2 = New-Object System.Windows.Forms.Panel
    $panel2.Dock = "Fill"
    $lbl2a = New-Object System.Windows.Forms.Label
    $lbl2a.Text = "Account Name:"
    $lbl2a.Location = New-Object System.Drawing.Point(30, 30)
    $lbl2a.Size = New-Object System.Drawing.Size(140, 20)
    $txtSam = New-Object System.Windows.Forms.TextBox
    $txtSam.Location = New-Object System.Drawing.Point(180, 27)
    $txtSam.Size = New-Object System.Drawing.Size(220, 20)
    $lbl2b = New-Object System.Windows.Forms.Label
    $lbl2b.Text = "Login (UPN):"
    $lbl2b.Location = New-Object System.Drawing.Point(30, 65)
    $lbl2b.Size = New-Object System.Drawing.Size(140, 20)
    $lblUpnValue = New-Object System.Windows.Forms.Label
    $lblUpnValue.Location = New-Object System.Drawing.Point(180, 65)
    $lblUpnValue.Size = New-Object System.Drawing.Size(350, 20)
    $lblUpnValue.ForeColor = [System.Drawing.Color]::DimGray
    $txtSam.add_TextChanged({ $lblUpnValue.Text = "$($txtSam.Text)@$($script:OnboardingUpnSuffix)" })
    $lbl2c = New-Object System.Windows.Forms.Label
    $lbl2c.Text = "Temporary Password:"
    $lbl2c.Location = New-Object System.Drawing.Point(30, 105)
    $lbl2c.Size = New-Object System.Drawing.Size(140, 20)
    $txtPass = New-Object System.Windows.Forms.TextBox
    $txtPass.Location = New-Object System.Drawing.Point(180, 102)
    $txtPass.Size = New-Object System.Drawing.Size(220, 20)
    $txtPass.PasswordChar = "*"
    $lbl2d = New-Object System.Windows.Forms.Label
    $lbl2d.Text = "Confirm Password:"
    $lbl2d.Location = New-Object System.Drawing.Point(30, 140)
    $lbl2d.Size = New-Object System.Drawing.Size(140, 20)
    $txtConfirm = New-Object System.Windows.Forms.TextBox
    $txtConfirm.Location = New-Object System.Drawing.Point(180, 137)
    $txtConfirm.Size = New-Object System.Drawing.Size(220, 20)
    $txtConfirm.PasswordChar = "*"
    $chkMustChange = New-Object System.Windows.Forms.CheckBox
    $chkMustChange.Text = "User must change password at next logon"
    $chkMustChange.Location = New-Object System.Drawing.Point(30, 175)
    $chkMustChange.Size = New-Object System.Drawing.Size(350, 24)
    $chkMustChange.Checked = $true
    $panel2.Controls.AddRange(@($lbl2a, $txtSam, $lbl2b, $lblUpnValue, $lbl2c, $txtPass, $lbl2d, $txtConfirm, $chkMustChange))

    # ---- Step 3: Organizational Unit ----
    $panel3 = New-Object System.Windows.Forms.Panel
    $panel3.Dock = "Fill"
    $lbl3a = New-Object System.Windows.Forms.Label
    $lbl3a.Text = "Organizational Unit:"
    $lbl3a.Location = New-Object System.Drawing.Point(30, 30)
    $lbl3a.Size = New-Object System.Drawing.Size(140, 20)
    $txtOuDisplay = New-Object System.Windows.Forms.TextBox
    $txtOuDisplay.Location = New-Object System.Drawing.Point(30, 55)
    $txtOuDisplay.Size = New-Object System.Drawing.Size(500, 20)
    $txtOuDisplay.ReadOnly = $true
    $txtOuDisplay.Text = "(not selected)"
    $btnBrowseOu = New-Object System.Windows.Forms.Button
    $btnBrowseOu.Text = "Browse..."
    $btnBrowseOu.Location = New-Object System.Drawing.Point(30, 85)
    $btnBrowseOu.Size = New-Object System.Drawing.Size(100, 28)
    $btnBrowseOu.add_Click({
        $picked = Show-OuPickerDialog
        if ($null -ne $picked) {
            $script:OnboardWizOuDn = $picked
            $txtOuDisplay.Text = $picked
        }
    })

    # Required - can't Finish without one (see the Step 2 validation block below). Sits here
    # rather than its own step since it's the same kind of one-off lookup as the OU itself.
    $lbl3b = New-Object System.Windows.Forms.Label
    $lbl3b.Text = "Manager:"
    $lbl3b.Location = New-Object System.Drawing.Point(30, 130)
    $lbl3b.Size = New-Object System.Drawing.Size(140, 20)
    $txtManagerDisplay = New-Object System.Windows.Forms.TextBox
    $txtManagerDisplay.Location = New-Object System.Drawing.Point(30, 155)
    $txtManagerDisplay.Size = New-Object System.Drawing.Size(500, 20)
    $txtManagerDisplay.ReadOnly = $true
    $txtManagerDisplay.Text = "(not selected)"
    $btnBrowseManager = New-Object System.Windows.Forms.Button
    $btnBrowseManager.Text = "Browse..."
    $btnBrowseManager.Location = New-Object System.Drawing.Point(30, 185)
    $btnBrowseManager.Size = New-Object System.Drawing.Size(100, 28)
    $btnBrowseManager.add_Click({
        $picked = Show-ManagerPickerDialog
        if ($null -ne $picked) {
            $script:OnboardWizManagerDn = $picked.DistinguishedName
            $script:OnboardWizManagerDisplay = "$($picked.FirstName) $($picked.LastName) ($($picked.SamAccountName))"
            $txtManagerDisplay.Text = $script:OnboardWizManagerDisplay
        }
    })

    $panel3.Controls.AddRange(@($lbl3a, $txtOuDisplay, $btnBrowseOu, $lbl3b, $txtManagerDisplay, $btnBrowseManager))

    # ---- Step 4: Security Groups ----
    $panel4 = New-Object System.Windows.Forms.Panel
    $panel4.Dock = "Fill"
    $lbl4a = New-Object System.Windows.Forms.Label
    $lbl4a.Text = "Security Groups:"
    $lbl4a.Location = New-Object System.Drawing.Point(30, 20)
    $lbl4a.Size = New-Object System.Drawing.Size(200, 20)
    $lstGroups = New-Object System.Windows.Forms.ListBox
    $lstGroups.Location = New-Object System.Drawing.Point(30, 45)
    $lstGroups.Size = New-Object System.Drawing.Size(400, 200)
    $btnAddGroups = New-Object System.Windows.Forms.Button
    $btnAddGroups.Text = "Add Groups..."
    $btnAddGroups.Location = New-Object System.Drawing.Point(440, 45)
    $btnAddGroups.Size = New-Object System.Drawing.Size(110, 28)
    $btnAddGroups.add_Click({
        $picked = @(Show-GroupPickerDialog -ExcludeNames @($wizGroups))
        foreach ($g in $picked) { [void]$wizGroups.Add($g) }
        $lstGroups.Items.Clear()
        foreach ($g in $wizGroups) { [void]$lstGroups.Items.Add($g) }
    })
    $btnRemoveGroup = New-Object System.Windows.Forms.Button
    $btnRemoveGroup.Text = "Remove Selected"
    $btnRemoveGroup.Location = New-Object System.Drawing.Point(440, 80)
    $btnRemoveGroup.Size = New-Object System.Drawing.Size(110, 28)
    $btnRemoveGroup.add_Click({
        if ($lstGroups.SelectedItem) {
            [void]$wizGroups.Remove($lstGroups.SelectedItem)
            $lstGroups.Items.Remove($lstGroups.SelectedItem)
        }
    })
    $panel4.Controls.AddRange(@($lbl4a, $lstGroups, $btnAddGroups, $btnRemoveGroup))

    # ---- Step 5: Computer Assignment ----
    $panel5 = New-Object System.Windows.Forms.Panel
    $panel5.Dock = "Fill"
    $lbl5a = New-Object System.Windows.Forms.Label
    $lbl5a.Text = "Computer Name:"
    $lbl5a.Location = New-Object System.Drawing.Point(30, 30)
    $lbl5a.Size = New-Object System.Drawing.Size(140, 20)
    $txtComputer = New-Object System.Windows.Forms.TextBox
    $txtComputer.Location = New-Object System.Drawing.Point(180, 27)
    $txtComputer.Size = New-Object System.Drawing.Size(220, 20)
    $btnCheckComputer = New-Object System.Windows.Forms.Button
    $btnCheckComputer.Text = "Check"
    $btnCheckComputer.Location = New-Object System.Drawing.Point(410, 25)
    $btnCheckComputer.Size = New-Object System.Drawing.Size(80, 26)
    $btnBrowseComputer = New-Object System.Windows.Forms.Button
    $btnBrowseComputer.Text = "Browse..."
    $btnBrowseComputer.Location = New-Object System.Drawing.Point(500, 25)
    $btnBrowseComputer.Size = New-Object System.Drawing.Size(90, 26)
    $lblComputerStatus = New-Object System.Windows.Forms.Label
    $lblComputerStatus.Location = New-Object System.Drawing.Point(30, 65)
    $lblComputerStatus.Size = New-Object System.Drawing.Size(550, 60)
    $checkComputer = {
        $name = $txtComputer.Text.Trim()
        if ([string]::IsNullOrWhiteSpace($name)) {
            $lblComputerStatus.Text = "Enter a computer name to check."
            $script:OnboardWizComputerChecked = $false
            return
        }
        $adParams = Get-AdCredParams
        $safeName = $name.Replace("'", "''")
        try {
            $adComputer = Get-ADComputer @adParams -SearchBase $script:ComputersOuDn -Filter "Name -eq '$safeName'" -ErrorAction Stop | Select-Object -First 1
        } catch {
            [System.Windows.Forms.MessageBox]::Show("Could not query Active Directory: $($_.Exception.Message)", "Active Directory Error", "OK", "Error") | Out-Null
            return
        }
        $script:OnboardWizComputerChecked = $true
        $script:OnboardWizComputerFound = ($null -ne $adComputer)
        $existingInventory = $null -ne ($script:Items.Rows | Where-Object { "$($_.DeviceName)".Trim().ToLowerInvariant() -eq $name.ToLowerInvariant() } | Select-Object -First 1)
        if ($script:OnboardWizComputerFound) {
            $lblComputerStatus.ForeColor = [System.Drawing.Color]::DarkGreen
            $lblComputerStatus.Text = "Found '$name' in Active Directory. Its Description will be updated.`n" +
                $(if ($existingInventory) { "An existing inventory record for '$name' will be updated." } else { "A new inventory record will be created for '$name'." })
        } else {
            $lblComputerStatus.ForeColor = [System.Drawing.Color]::DarkOrange
            $lblComputerStatus.Text = "'$name' was NOT found in Active Directory under $($script:ComputersOuDn).`n" +
                "You can still continue - the AD user and inventory record will be created, but the computer's AD Description won't be updated until it's joined and linked later (e.g. via Match User)."
        }
    }
    $btnCheckComputer.add_Click($checkComputer)
    $btnBrowseComputer.add_Click({
        # Same "available device" pool Transfer Computer uses (blank Description, any tab,
        # including Unknown) - picking one here fills in the name and immediately runs the
        # same AD check Check does, so Browse feels like one complete action, not two.
        $picked = Show-TransferTargetDialog -Title "Select Computer to Onboard"
        if ($null -ne $picked) {
            $txtComputer.Text = "$($picked.DeviceName)"
            & $checkComputer
        }
    })
    $panel5.Controls.AddRange(@($lbl5a, $txtComputer, $btnCheckComputer, $btnBrowseComputer, $lblComputerStatus))

    # ---- Step 6: Inventory Details ----
    $panel6 = New-Object System.Windows.Forms.Panel
    $panel6.Dock = "Fill"
    $panel6.AutoScroll = $true
    $inventoryFieldDefs = @($script:VisibleFieldDefs | Where-Object { $_.Key -notin @("DeviceName", "LastName", "FirstName", "Description", "Username") })
    $wizFieldControls = @{}
    $fy = 20
    foreach ($f in $inventoryFieldDefs) {
        $flbl = New-Object System.Windows.Forms.Label
        $flbl.Text = "$($f.Label):"
        $flbl.Location = New-Object System.Drawing.Point(30, $fy)
        $flbl.Size = New-Object System.Drawing.Size(140, 20)
        $panel6.Controls.Add($flbl)
        if ($f.Type -eq "dropdown") {
            $fctrl = New-Object System.Windows.Forms.ComboBox
            $fctrl.Location = New-Object System.Drawing.Point(180, ($fy - 3))
            $fctrl.Size = New-Object System.Drawing.Size(220, 20)
            $fctrl.DropDownStyle = "DropDownList"
            [void]$fctrl.Items.AddRange($f.Options)
            $fctrl.SelectedItem = $f.Default
        } elseif ($f.Type -eq "dynamicdropdown") {
            # Same editable-combo treatment as Show-ItemDialog (Add/Edit Item) - options come
            # from whatever's already in the inventory, and typing a new value is still allowed.
            $fctrl = New-Object System.Windows.Forms.ComboBox
            $fctrl.Location = New-Object System.Drawing.Point(180, ($fy - 3))
            $fctrl.Size = New-Object System.Drawing.Size(220, 20)
            $fctrl.DropDownStyle = "DropDown"
            [void]$fctrl.Items.AddRange((Get-DistinctFieldValues $f.Key))
        } else {
            $fctrl = New-Object System.Windows.Forms.TextBox
            $fctrl.Location = New-Object System.Drawing.Point(180, ($fy - 3))
            $fctrl.Size = New-Object System.Drawing.Size(220, 20)
        }
        $panel6.Controls.Add($fctrl)
        $wizFieldControls[$f.Key] = $fctrl
        $fy += 32
    }

    # ---- Step 7: Review & Create ----
    $panel7 = New-Object System.Windows.Forms.Panel
    $panel7.Dock = "Fill"
    $txtReview = New-Object System.Windows.Forms.TextBox
    $txtReview.Location = New-Object System.Drawing.Point(30, 20)
    $txtReview.Size = New-Object System.Drawing.Size(600, 320)
    $txtReview.Multiline = $true
    $txtReview.ReadOnly = $true
    $txtReview.ScrollBars = "Vertical"
    $txtReview.Font = New-Object System.Drawing.Font("Consolas", 9)
    $panel7.Controls.Add($txtReview)

    $steps = @($panel1, $panel2, $panel3, $panel4, $panel5, $panel6, $panel7)
    $stepTitles = @("Basic Info", "Account Details", "Organizational Unit", "Security Groups", "Computer Assignment", "Inventory Details", "Review & Create")
    foreach ($p in $steps) { $p.Visible = $false; $contentPanel.Controls.Add($p) }

    # A right-to-left FlowLayoutPanel, not hardcoded pixel positions, so the buttons always sit
    # correctly against the right edge regardless of the dialog's actual client width (which is
    # narrower than Form.Size due to window chrome) or if the user resizes it - fixed-position
    # buttons here previously ran off the visible edge of the dialog.
    $navPanel = New-Object System.Windows.Forms.Panel
    $navPanel.Dock = "Bottom"
    $navPanel.Height = 50

    $navFlow = New-Object System.Windows.Forms.FlowLayoutPanel
    $navFlow.Dock = "Right"
    $navFlow.FlowDirection = "RightToLeft"
    $navFlow.WrapContents = $false
    $navFlow.AutoSize = $true
    $navFlow.AutoSizeMode = "GrowAndShrink"
    $navFlow.Padding = New-Object System.Windows.Forms.Padding(0, 10, 20, 0)

    $btnCancelWiz = New-Object System.Windows.Forms.Button
    $btnCancelWiz.Text = "Cancel"
    $btnCancelWiz.Size = New-Object System.Drawing.Size(90, 30)
    $btnCancelWiz.Margin = New-Object System.Windows.Forms.Padding(8, 0, 0, 0)
    $btnCancelWiz.add_Click({ $dlg.Close() })

    $btnNext = New-Object System.Windows.Forms.Button
    $btnNext.Text = "Next >"
    $btnNext.Size = New-Object System.Drawing.Size(90, 30)
    $btnNext.Margin = New-Object System.Windows.Forms.Padding(8, 0, 0, 0)

    $btnBack = New-Object System.Windows.Forms.Button
    $btnBack.Text = "< Back"
    $btnBack.Size = New-Object System.Drawing.Size(90, 30)
    $btnBack.Margin = New-Object System.Windows.Forms.Padding(8, 0, 0, 0)

    # RightToLeft flow lays out in the order added, starting from the right - so adding
    # Cancel first puts it rightmost, then Next to its left, then Back furthest left,
    # reproducing the original left-to-right visual order (Back, Next, Cancel).
    $navFlow.Controls.Add($btnCancelWiz)
    $navFlow.Controls.Add($btnNext)
    $navFlow.Controls.Add($btnBack)
    $navPanel.Controls.Add($navFlow)

    $showStep = {
        foreach ($p in $steps) { $p.Visible = $false }
        $steps[$script:OnboardWizStep].Visible = $true
        $lblStepTitle.Text = "Step $($script:OnboardWizStep + 1) of $($steps.Count): $($stepTitles[$script:OnboardWizStep])"
        $btnBack.Enabled = ($script:OnboardWizStep -gt 0)
        $btnNext.Text = if ($script:OnboardWizStep -eq $steps.Count - 1) { "Finish" } else { "Next >" }
    }

    $btnBack.add_Click({
        if ($script:OnboardWizStep -gt 0) {
            $script:OnboardWizStep = $script:OnboardWizStep - 1
            & $showStep
        }
    })

    $doFinish = {
        $firstName = $txtFirstName.Text.Trim()
        $lastName = $txtLastName.Text.Trim()
        $samName = $txtSam.Text.Trim()
        $upn = "$samName@$($script:OnboardingUpnSuffix)"
        $password = $txtPass.Text
        $computerName = $txtComputer.Text.Trim()
        $fullName = "$firstName $lastName".Trim()
        $ticketNumber = $txtOnboardTicket.Text.Trim()
        $userDescription = $txtOnboardDescription.Text.Trim()
        $userOffice = $txtOnboardOffice.Text.Trim()

        # Hard stop, independent of the picker's own filtering above - the account is never
        # created outside the allowed OU subtree, no matter how $script:OnboardWizOuDn got set.
        if (-not (Test-OuIsAllowedForNewUsers $script:OnboardWizOuDn)) {
            [System.Windows.Forms.MessageBox]::Show("The selected Organizational Unit is not allowed for new users. Users may only be created in a sub-OU of '$($script:AllowedUserOuBaseDn)' - never in that OU directly. Nothing was changed.", "Onboarding Blocked", "OK", "Error") | Out-Null
            return
        }
        # Same kind of hard stop as the OU check above - Manager is required by the Step 2
        # validation already, but this is the last line of defense before the AD write.
        if ([string]::IsNullOrWhiteSpace($script:OnboardWizManagerDn)) {
            [System.Windows.Forms.MessageBox]::Show("No Manager was selected. Nothing was changed.", "Onboarding Blocked", "OK", "Error") | Out-Null
            return
        }

        $dlg.Cursor = [System.Windows.Forms.Cursors]::WaitCursor
        $adParams = Get-AdCredParams
        try {
            try {
                $securePass = ConvertTo-SecureString $password -AsPlainText -Force

                # Who created this account, and why - stamped into the AD "info" attribute
                # (the Notes box under ADUC's Telephones tab) so a manager who opens the
                # profile later can see when it was created, under what ticket, and by whom,
                # without needing to go dig through this app's own logs. Every later event
                # (Transfer User, Offboard User) appends its own timestamped entry here via
                # Add-AdUserNoteLine, building a running history on the account itself.
                $creatorDisplay = Get-AdActorDisplayName -AdParams $adParams
                $notesText = Format-AdNoteEntry "Onboarded by $creatorDisplay - Ticket # $ticketNumber"

                # Email and proxyAddresses both mirror the UPN, per explicit request - the
                # primary SMTP proxy address (uppercase "SMTP:", the Exchange/AD convention for
                # the PRIMARY address as opposed to a lowercase "smtp:" alias) is set via
                # -OtherAttributes in the same creation call, rather than a separate Set-ADUser
                # afterward, so the account never briefly exists without it. The provenance
                # marker (see $script:OnboardingMarkerAttribute above) rides along in the same
                # call for the same reason - never a moment where the account exists unmarked.
                $otherAttrs = @{ proxyAddresses = @("SMTP:$upn"); info = $notesText }
                $otherAttrs[$script:OnboardingMarkerAttribute] = $script:OnboardingMarkerValue
                New-ADUser @adParams -Name $fullName -GivenName $firstName -Surname $lastName `
                    -SamAccountName $samName -UserPrincipalName $upn -EmailAddress $upn `
                    -OtherAttributes $otherAttrs `
                    -Path $script:OnboardWizOuDn -Manager $script:OnboardWizManagerDn `
                    -Description $userDescription -Office $userOffice `
                    -AccountPassword $securePass -Enabled $true -ChangePasswordAtLogon $chkMustChange.Checked `
                    -ErrorAction Stop
                Write-AppLog "Onboarding: created AD user '$samName' ($fullName), email/proxyAddress set to '$upn', manager '$($script:OnboardWizManagerDisplay)'." "Info"
            } catch {
                [System.Windows.Forms.MessageBox]::Show("Could not create the AD user account: $($_.Exception.Message)`n`nNothing else was changed - fix the issue and try again.", "Onboarding Failed", "OK", "Error") | Out-Null
                return
            }

            foreach ($g in $wizGroups) {
                # Hard stop per group, independent of the picker's own filtering above - never
                # add the new account to a privileged group, no matter how $wizGroups got
                # populated.
                if (Test-GroupIsPrivileged -GroupName $g -AdParams $adParams) {
                    Write-AppLog "Onboarding: refused to add '$samName' to '$g' - it is a privileged/denied group. Add it manually if this is genuinely intended." "Error"
                    continue
                }
                try {
                    Add-ADGroupMember @adParams -Identity $g -Members $samName -ErrorAction Stop
                    Write-AppLog "Onboarding: added '$samName' to group '$g'." "Info"
                } catch {
                    Write-AppLog "Onboarding: user was created, but could NOT be added to group '$g': $($_.Exception.Message)" "Error"
                }
            }

            $newDescription = "$fullName***"
            if ($script:OnboardWizComputerFound) {
                Sync-AdComputerDescription -DeviceName $computerName -Description $newDescription
            }

            $existingRow = $script:Items.Rows | Where-Object { "$($_.DeviceName)".Trim().ToLowerInvariant() -eq $computerName.ToLowerInvariant() } | Select-Object -First 1
            if ($null -eq $existingRow) {
                $existingRow = $script:Items.NewRow()
                $existingRow["Id"] = Get-NextId
                $existingRow["Status"] = "Current"
                $existingRow["DeviceName"] = $computerName
                $script:Items.Rows.Add($existingRow)
            } else {
                $existingRow["Status"] = "Current"
            }
            $existingRow["Username"] = $samName
            $existingRow["FirstName"] = $firstName
            $existingRow["LastName"] = $lastName
            $existingRow["Description"] = $newDescription
            $existingRow["ADFlagReason"] = ""
            $existingRow["ADComputerDisabled"] = ""
            $existingRow["ADComputerNotFound"] = if ($script:OnboardWizComputerFound) { "" } else { "No matching AD computer object found" }
            foreach ($f in $inventoryFieldDefs) {
                $ctrl = $wizFieldControls[$f.Key]
                $existingRow[$f.Key] = if ($f.Type -eq "dropdown") { "$($ctrl.SelectedItem)" } else { $ctrl.Text.Trim() }
            }

            Save-Inventory
            Refresh-AllTabs
            Write-AppLog "Onboarding complete: $fullName ($samName) on '$computerName'." "Info"
            [System.Windows.Forms.MessageBox]::Show("Onboarding complete for $fullName.`n`nAccount: $samName`nComputer: $computerName", "Onboarding Complete", "OK", "Information") | Out-Null
            $dlg.Close()
        } finally {
            $dlg.Cursor = [System.Windows.Forms.Cursors]::Default
        }
    }

    $btnNext.add_Click({
        $proceed = $true
        switch ($script:OnboardWizStep) {
            0 {
                if ([string]::IsNullOrWhiteSpace($txtOnboardTicket.Text)) {
                    [System.Windows.Forms.MessageBox]::Show("Enter a ticket number.", "Onboard New User", "OK", "Warning") | Out-Null
                    $proceed = $false
                } elseif ([string]::IsNullOrWhiteSpace($txtFirstName.Text) -or [string]::IsNullOrWhiteSpace($txtLastName.Text)) {
                    [System.Windows.Forms.MessageBox]::Show("Enter both a First Name and Last Name.", "Onboard New User", "OK", "Warning") | Out-Null
                    $proceed = $false
                } elseif ([string]::IsNullOrWhiteSpace($txtSam.Text)) {
                    # Only auto-generate once - if the admin already edited it (including via
                    # Back/Next), don't clobber their edit.
                    $dlg.Cursor = [System.Windows.Forms.Cursors]::WaitCursor
                    try {
                        $adParams = Get-AdCredParams
                        $base = Get-SanitizedAccountNameBase -LastName $txtLastName.Text.Trim() -FirstName $txtFirstName.Text.Trim()
                        $txtSam.Text = Get-UniqueSamAccountName -BaseName $base -AdParams $adParams
                    } catch {
                        [System.Windows.Forms.MessageBox]::Show("Could not check Active Directory for account name availability: $($_.Exception.Message)", "Active Directory Error", "OK", "Error") | Out-Null
                        $proceed = $false
                    } finally {
                        $dlg.Cursor = [System.Windows.Forms.Cursors]::Default
                    }
                }
            }
            1 {
                if ([string]::IsNullOrWhiteSpace($txtSam.Text)) {
                    [System.Windows.Forms.MessageBox]::Show("Enter an account name.", "Onboard New User", "OK", "Warning") | Out-Null
                    $proceed = $false
                } elseif ([string]::IsNullOrWhiteSpace($txtPass.Text)) {
                    [System.Windows.Forms.MessageBox]::Show("Enter a temporary password.", "Onboard New User", "OK", "Warning") | Out-Null
                    $proceed = $false
                } elseif ($txtPass.Text -ne $txtConfirm.Text) {
                    [System.Windows.Forms.MessageBox]::Show("Passwords do not match.", "Onboard New User", "OK", "Warning") | Out-Null
                    $proceed = $false
                }
            }
            2 {
                if ([string]::IsNullOrWhiteSpace($script:OnboardWizOuDn)) {
                    [System.Windows.Forms.MessageBox]::Show("Choose an Organizational Unit.", "Onboard New User", "OK", "Warning") | Out-Null
                    $proceed = $false
                } elseif ([string]::IsNullOrWhiteSpace($script:OnboardWizManagerDn)) {
                    [System.Windows.Forms.MessageBox]::Show("Choose a Manager.", "Onboard New User", "OK", "Warning") | Out-Null
                    $proceed = $false
                }
            }
            4 {
                if ([string]::IsNullOrWhiteSpace($txtComputer.Text)) {
                    [System.Windows.Forms.MessageBox]::Show("Enter a computer name.", "Onboard New User", "OK", "Warning") | Out-Null
                    $proceed = $false
                } else {
                    & $checkComputer
                }
            }
            6 {
                & $doFinish
                return
            }
        }
        if ($proceed) {
            if ($script:OnboardWizStep -eq 5) {
                $groupsText = if ($wizGroups.Count -eq 0) { "(none)" } else { ($wizGroups -join "`n  ") }
                $txtReview.Text =
                    "Name:      $($txtFirstName.Text.Trim()) $($txtLastName.Text.Trim())`r`n" +
                    "Account:   $($txtSam.Text.Trim())`r`n" +
                    "Login:     $($lblUpnValue.Text)`r`n" +
                    "Email:     $($lblUpnValue.Text)`r`n" +
                    "Proxy Address: SMTP:$($lblUpnValue.Text)`r`n" +
                    "Must change password at next logon: $($chkMustChange.Checked)`r`n" +
                    "OU:        $($script:OnboardWizOuDn)`r`n" +
                    "Manager:   $($script:OnboardWizManagerDisplay)`r`n" +
                    "Ticket #:  $(if ([string]::IsNullOrWhiteSpace($txtOnboardTicket.Text)) { '(none)' } else { $txtOnboardTicket.Text.Trim() })`r`n" +
                    "Description: $(if ([string]::IsNullOrWhiteSpace($txtOnboardDescription.Text)) { '(none)' } else { $txtOnboardDescription.Text.Trim() })`r`n" +
                    "Office:    $(if ([string]::IsNullOrWhiteSpace($txtOnboardOffice.Text)) { '(none)' } else { $txtOnboardOffice.Text.Trim() })`r`n" +
                    "Groups:`r`n  $groupsText`r`n" +
                    "Computer:  $($txtComputer.Text.Trim()) $(if ($script:OnboardWizComputerFound) { '(found in AD)' } else { '(NOT found in AD)' })`r`n`r`n" +
                    "Review the above, then click Finish to create the account, assign groups, and save the inventory record."
            }
            $script:OnboardWizStep = $script:OnboardWizStep + 1
            & $showStep
        }
    })

    $dlg.Controls.Add($contentPanel)
    $dlg.Controls.Add($navPanel)
    $dlg.Controls.Add($lblStepTitle)
    # Enter advances to the next step (or triggers Finish on the last one) from anywhere on the
    # form, same as the OU/Group/Transfer pickers already do - $btnNext's Text changes to
    # "Finish" on the last step, but it's the same button/handler throughout.
    $dlg.AcceptButton = $btnNext

    & $showStep
    [void]$dlg.ShowDialog()
    $dlg.Dispose()
}

function Invoke-MatchUserToAd([string]$StatusKey) {
    $grid = $script:Grids[$StatusKey]
    if ($grid.SelectedRows.Count -eq 0) {
        [System.Windows.Forms.MessageBox]::Show("Select one or more devices to match.", "No Selection", "OK", "Information") | Out-Null
        return
    }
    if (-not (Test-AdModuleAvailable)) { return }

    $ids = @($grid.SelectedRows | Sort-Object Index | ForEach-Object { $_.DataBoundItem["Id"] })
    $adParams = Get-AdCredParams
    $matchedCount = 0; $ambiguousCount = 0; $notFoundCount = 0; $errorCount = 0

    foreach ($id in $ids) {
        $dataRow = $script:Items.Rows.Find($id)
        if ($null -eq $dataRow) { continue }
        $deviceName = "$($dataRow.DeviceName)"
        $username = "$($dataRow.Username)".Trim()
        $lastName = "$($dataRow.LastName)".Trim()
        $firstName = "$($dataRow.FirstName)".Trim()
        $adUser = $null

        try {
            if (-not [string]::IsNullOrWhiteSpace($username)) {
                # Already anchored - just refresh from AD to pick up any name changes there.
                $adUser = Get-ADUser @adParams -Identity $username -Properties GivenName, Surname -ErrorAction Stop
            } elseif ([string]::IsNullOrWhiteSpace($lastName)) {
                Write-AppLog "Match User - '$deviceName': no Last Name on record to search AD by." "Warning"
                $notFoundCount++
                continue
            } else {
                $safeLast = $lastName.Replace("'", "''")
                $candidates = @(Get-ADUser @adParams -Filter "Surname -eq '$safeLast'" -Properties GivenName, Surname -ErrorAction Stop)
                if ($candidates.Count -eq 0) {
                    Write-AppLog "Match User - '$deviceName': no AD user found with Last Name '$lastName'." "Warning"
                    $notFoundCount++
                    continue
                } elseif ($candidates.Count -eq 1) {
                    $adUser = $candidates[0]
                } else {
                    if (-not [string]::IsNullOrWhiteSpace($firstName)) {
                        $narrowed = @($candidates | Where-Object { "$($_.GivenName)".Trim().ToLowerInvariant() -eq $firstName.ToLowerInvariant() })
                        if ($narrowed.Count -eq 1) { $adUser = $narrowed[0] }
                    }
                    if ($null -eq $adUser) {
                        $picked = Show-AdUserPickerDialog -Candidates $candidates -DeviceName $deviceName
                        if ($null -eq $picked) {
                            Write-AppLog "Match User - '$deviceName': $($candidates.Count) AD users named '$lastName' found, none selected." "Warning"
                            $ambiguousCount++
                            continue
                        }
                        $adUser = $picked
                    }
                }
            }
        } catch {
            Write-AppLog "Match User - '$deviceName': AD lookup failed: $($_.Exception.Message)" "Error"
            $errorCount++
            continue
        }

        $dataRow["Username"] = "$($adUser.SamAccountName)"
        $dataRow["FirstName"] = "$($adUser.GivenName)"
        $dataRow["LastName"] = "$($adUser.Surname)"
        Write-AppLog "Match User - '$deviceName': matched to $($adUser.GivenName) $($adUser.Surname) ($($adUser.SamAccountName))." "Info"
        $matchedCount++
    }

    Save-Inventory
    Refresh-AllTabs
    [System.Windows.Forms.MessageBox]::Show(
        "Matched $matchedCount device(s) to AD.`n$ambiguousCount needed manual selection and were skipped, $notFoundCount had no AD match, $errorCount errored.`n`nSee the Activity Log for details.",
        "Match User Complete", "OK", "Information") | Out-Null
}

function Show-AdUserSearchDialog {
    if (-not (Test-AdModuleAvailable)) { return }

    $dlg = New-Object System.Windows.Forms.Form
    $dlg.Text = "Search Active Directory"
    $dlg.Size = New-Object System.Drawing.Size(600, 450)
    $dlg.StartPosition = "CenterParent"
    $dlg.MinimumSize = New-Object System.Drawing.Size(500, 300)

    $grid = New-Object System.Windows.Forms.DataGridView
    $grid.Dock = "Fill"
    $grid.ReadOnly = $true
    $grid.AllowUserToAddRows = $false
    $grid.AutoSizeColumnsMode = "Fill"

    $topPanel = New-Object System.Windows.Forms.Panel
    $topPanel.Dock = "Top"
    $topPanel.Height = 40

    $lblSearch = New-Object System.Windows.Forms.Label
    $lblSearch.Text = "Name contains:"
    $lblSearch.Location = New-Object System.Drawing.Point(10, 12)
    $lblSearch.Size = New-Object System.Drawing.Size(90, 20)
    $topPanel.Controls.Add($lblSearch)

    $txtSearch = New-Object System.Windows.Forms.TextBox
    $txtSearch.Location = New-Object System.Drawing.Point(105, 9)
    $txtSearch.Size = New-Object System.Drawing.Size(320, 20)
    $topPanel.Controls.Add($txtSearch)

    $btnSearch = New-Object System.Windows.Forms.Button
    $btnSearch.Text = "Search"
    $btnSearch.Location = New-Object System.Drawing.Point(435, 7)
    $btnSearch.Size = New-Object System.Drawing.Size(90, 26)
    $topPanel.Controls.Add($btnSearch)

    $bottomPanel = New-Object System.Windows.Forms.Panel
    $bottomPanel.Dock = "Bottom"
    $bottomPanel.Height = 40

    $lblCount = New-Object System.Windows.Forms.Label
    $lblCount.Text = "Type part of a name (first, last, or both) and press Search or Enter."
    $lblCount.Location = New-Object System.Drawing.Point(10, 12)
    $lblCount.Size = New-Object System.Drawing.Size(420, 20)
    $bottomPanel.Controls.Add($lblCount)

    $btnClose = New-Object System.Windows.Forms.Button
    $btnClose.Text = "Close"
    $btnClose.Location = New-Object System.Drawing.Point(490, 6)
    $btnClose.Size = New-Object System.Drawing.Size(80, 28)
    $btnClose.add_Click({ $dlg.Close() })
    $bottomPanel.Controls.Add($btnClose)

    $dlg.Controls.Add($grid)
    $dlg.Controls.Add($bottomPanel)
    $dlg.Controls.Add($topPanel)

    $doSearch = {
        $term = $txtSearch.Text.Trim()
        if ([string]::IsNullOrWhiteSpace($term)) {
            [System.Windows.Forms.MessageBox]::Show("Enter part of a name to search for.", "Search Active Directory", "OK", "Information") | Out-Null
            return
        }
        $dlg.Cursor = [System.Windows.Forms.Cursors]::WaitCursor
        try {
            $adParams = Get-AdCredParams
            $safe = $term.Replace("'", "''")
            $filter = "Name -like '*$safe*' -or GivenName -like '*$safe*' -or Surname -like '*$safe*'"
            $results = @(Get-ADUser @adParams -Filter $filter -Properties GivenName, Surname, Enabled, SamAccountName |
                Sort-Object Surname, GivenName |
                ForEach-Object { [PSCustomObject]@{ FirstName = $_.GivenName; LastName = $_.Surname; SamAccountName = $_.SamAccountName; Enabled = $_.Enabled } })
            $grid.DataSource = [System.Collections.ArrayList]$results
            $lblCount.Text = "$($results.Count) match(es) found."
        } catch {
            [System.Windows.Forms.MessageBox]::Show("Search failed: $($_.Exception.Message)", "Active Directory Error", "OK", "Error") | Out-Null
        } finally {
            $dlg.Cursor = [System.Windows.Forms.Cursors]::Default
        }
    }

    $btnSearch.add_Click($doSearch)
    $txtSearch.add_KeyDown({
        param($sender, $e)
        if ($e.KeyCode -eq [System.Windows.Forms.Keys]::Enter) {
            & $doSearch
            $e.SuppressKeyPress = $true
        }
    })

    [void]$dlg.ShowDialog()
    $dlg.Dispose()
}

function Show-ManagerPickerDialog {
    if (-not (Test-AdModuleAvailable)) { return $null }

    $dlg = New-Object System.Windows.Forms.Form
    $dlg.Text = "Select Manager"
    $dlg.Size = New-Object System.Drawing.Size(600, 450)
    $dlg.StartPosition = "CenterParent"
    $dlg.MinimumSize = New-Object System.Drawing.Size(500, 300)

    $topPanel = New-Object System.Windows.Forms.Panel
    $topPanel.Dock = "Top"
    $topPanel.Height = 40

    $lblSearch = New-Object System.Windows.Forms.Label
    $lblSearch.Text = "Name contains:"
    $lblSearch.Location = New-Object System.Drawing.Point(10, 12)
    $lblSearch.Size = New-Object System.Drawing.Size(90, 20)
    $topPanel.Controls.Add($lblSearch)

    $txtSearch = New-Object System.Windows.Forms.TextBox
    $txtSearch.Location = New-Object System.Drawing.Point(105, 9)
    $txtSearch.Size = New-Object System.Drawing.Size(320, 20)
    $topPanel.Controls.Add($txtSearch)

    $btnSearch = New-Object System.Windows.Forms.Button
    $btnSearch.Text = "Search"
    $btnSearch.Location = New-Object System.Drawing.Point(435, 7)
    $btnSearch.Size = New-Object System.Drawing.Size(90, 26)
    $topPanel.Controls.Add($btnSearch)

    $grid = New-Object System.Windows.Forms.DataGridView
    $grid.Dock = "Fill"
    $grid.ReadOnly = $true
    $grid.AllowUserToAddRows = $false
    $grid.MultiSelect = $false
    $grid.SelectionMode = "FullRowSelect"
    $grid.AutoSizeColumnsMode = "Fill"

    $bottomPanel = New-Object System.Windows.Forms.Panel
    $bottomPanel.Dock = "Bottom"
    $bottomPanel.Height = 45

    $lblCount = New-Object System.Windows.Forms.Label
    $lblCount.Text = "Type part of a name (first, last, or both) and press Search or Enter."
    $lblCount.Location = New-Object System.Drawing.Point(10, 15)
    $lblCount.Size = New-Object System.Drawing.Size(390, 20)
    $bottomPanel.Controls.Add($lblCount)

    $btnSelect = New-Object System.Windows.Forms.Button
    $btnSelect.Text = "Select"
    $btnSelect.Location = New-Object System.Drawing.Point(410, 8)
    $btnSelect.Size = New-Object System.Drawing.Size(80, 28)
    $btnSelect.DialogResult = [System.Windows.Forms.DialogResult]::OK
    $bottomPanel.Controls.Add($btnSelect)

    $btnCancel = New-Object System.Windows.Forms.Button
    $btnCancel.Text = "Cancel"
    $btnCancel.Location = New-Object System.Drawing.Point(500, 8)
    $btnCancel.Size = New-Object System.Drawing.Size(80, 28)
    $btnCancel.DialogResult = [System.Windows.Forms.DialogResult]::Cancel
    $bottomPanel.Controls.Add($btnCancel)

    $dlg.Controls.Add($grid)
    $dlg.Controls.Add($bottomPanel)
    $dlg.Controls.Add($topPanel)
    $dlg.AcceptButton = $btnSelect
    $dlg.CancelButton = $btnCancel

    $doSearch = {
        $term = $txtSearch.Text.Trim()
        if ([string]::IsNullOrWhiteSpace($term)) {
            [System.Windows.Forms.MessageBox]::Show("Enter part of a name to search for.", "Select Manager", "OK", "Information") | Out-Null
            return
        }
        $dlg.Cursor = [System.Windows.Forms.Cursors]::WaitCursor
        try {
            $adParams = Get-AdCredParams
            $safe = $term.Replace("'", "''")
            $filter = "Name -like '*$safe*' -or GivenName -like '*$safe*' -or Surname -like '*$safe*'"
            $results = @(Get-ADUser @adParams -Filter $filter -Properties GivenName, Surname, SamAccountName, Title, DistinguishedName |
                Sort-Object Surname, GivenName |
                ForEach-Object { [PSCustomObject]@{ FirstName = $_.GivenName; LastName = $_.Surname; SamAccountName = $_.SamAccountName; Title = $_.Title; DistinguishedName = $_.DistinguishedName } })
            $grid.DataSource = [System.Collections.ArrayList]$results
            if ($grid.Columns["DistinguishedName"]) { $grid.Columns["DistinguishedName"].Visible = $false }
            $lblCount.Text = "$($results.Count) match(es) found."
        } catch {
            [System.Windows.Forms.MessageBox]::Show("Search failed: $($_.Exception.Message)", "Active Directory Error", "OK", "Error") | Out-Null
        } finally {
            $dlg.Cursor = [System.Windows.Forms.Cursors]::Default
        }
    }

    $btnSearch.add_Click($doSearch)
    $txtSearch.add_KeyDown({
        param($sender, $e)
        if ($e.KeyCode -eq [System.Windows.Forms.Keys]::Enter) {
            & $doSearch
            $e.SuppressKeyPress = $true
        }
    })

    $result = $null
    if ($dlg.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK -and $grid.SelectedRows.Count -eq 1) {
        $row = $grid.SelectedRows[0]
        $result = [PSCustomObject]@{
            FirstName         = "$($row.Cells["FirstName"].Value)"
            LastName          = "$($row.Cells["LastName"].Value)"
            SamAccountName    = "$($row.Cells["SamAccountName"].Value)"
            Title             = "$($row.Cells["Title"].Value)"
            DistinguishedName = "$($row.Cells["DistinguishedName"].Value)"
        }
    }
    $dlg.Dispose()
    return $result
}

function Remove-InventoryRowsById([array]$Ids) {
    $removed = 0
    foreach ($id in $Ids) {
        $dataRow = $script:Items.Rows.Find($id)
        if ($null -ne $dataRow) { $script:Items.Rows.Remove($dataRow); $removed++ }
    }
    if ($removed -gt 0) {
        Save-Inventory
        Refresh-AllTabs
    }
    return $removed
}

function Show-RowInMainGrid([int]$Id, [string]$StatusKey) {
    for ($i = 0; $i -lt $script:Statuses.Count; $i++) {
        if ($script:Statuses[$i].Key -eq $StatusKey) { $script:TabControl.SelectedIndex = $i; break }
    }
    $script:SearchBoxes[$StatusKey].Text = ""
    Apply-Filter -StatusKey $StatusKey
    $grid = $script:Grids[$StatusKey]
    foreach ($row in $grid.Rows) {
        if ($row.DataBoundItem["Id"] -eq $Id) {
            $grid.ClearSelection()
            $row.Selected = $true
            $firstVisible = $grid.Columns | Where-Object { $_.Visible } | Select-Object -First 1
            if ($firstVisible) { $grid.CurrentCell = $row.Cells[$firstVisible.Index] }
            $grid.FirstDisplayedScrollingRowIndex = [Math]::Max(0, $row.Index - 3)
            break
        }
    }
}

function Show-DuplicateDevicesDialog {
    $dlg = New-Object System.Windows.Forms.Form
    $dlg.Text = "Duplicate Devices"
    $dlg.Size = New-Object System.Drawing.Size(900, 550)
    $dlg.StartPosition = "CenterParent"
    $dlg.MinimumSize = New-Object System.Drawing.Size(700, 400)

    $grid = New-Object System.Windows.Forms.DataGridView
    $grid.Dock = "Fill"
    $grid.ReadOnly = $true
    $grid.AllowUserToAddRows = $false
    $grid.MultiSelect = $true
    $grid.SelectionMode = "FullRowSelect"
    $grid.AutoSizeColumnsMode = "Fill"

    $bottomPanel = New-Object System.Windows.Forms.Panel
    $bottomPanel.Dock = "Bottom"
    $bottomPanel.Height = 45

    $lblInfo = New-Object System.Windows.Forms.Label
    $lblInfo.Location = New-Object System.Drawing.Point(10, 15)
    $lblInfo.Size = New-Object System.Drawing.Size(500, 20)
    $bottomPanel.Controls.Add($lblInfo)

    $btnGoTo = New-Object System.Windows.Forms.Button
    $btnGoTo.Text = "Go to Selected"
    $btnGoTo.Location = New-Object System.Drawing.Point(520, 8)
    $btnGoTo.Size = New-Object System.Drawing.Size(110, 28)
    $bottomPanel.Controls.Add($btnGoTo)

    $btnDelete = New-Object System.Windows.Forms.Button
    $btnDelete.Text = "Delete Selected"
    $btnDelete.Location = New-Object System.Drawing.Point(640, 8)
    $btnDelete.Size = New-Object System.Drawing.Size(110, 28)
    $bottomPanel.Controls.Add($btnDelete)

    $btnRefresh = New-Object System.Windows.Forms.Button
    $btnRefresh.Text = "Refresh"
    $btnRefresh.Location = New-Object System.Drawing.Point(760, 8)
    $btnRefresh.Size = New-Object System.Drawing.Size(80, 28)
    $bottomPanel.Controls.Add($btnRefresh)

    $dlg.Controls.Add($grid)
    $dlg.Controls.Add($bottomPanel)

    $runDupCheck = {
        $groups = $script:Items.Rows |
            Where-Object { -not [string]::IsNullOrWhiteSpace("$($_.DeviceName)") } |
            Group-Object { "$($_.DeviceName)".Trim().ToLowerInvariant() }
        $dupGroups = @($groups | Where-Object { $_.Count -gt 1 })

        $displayRows = @()
        foreach ($g in $dupGroups) {
            foreach ($row in $g.Group) {
                $statusLabel = ($script:Statuses | Where-Object { $_.Key -eq $row.Status }).Label
                $displayRows += [PSCustomObject]@{
                    Id = $row.Id
                    DeviceName = $row.DeviceName
                    LastName = $row.LastName
                    FirstName = $row.FirstName
                    Tab = $statusLabel
                    StatusKey = $row.Status
                    SerialNumber = $row.SerialNumber
                }
            }
        }
        $displayRows = @($displayRows | Sort-Object DeviceName)
        $grid.DataSource = [System.Collections.ArrayList]$displayRows
        if ($grid.Columns["StatusKey"]) { $grid.Columns["StatusKey"].Visible = $false }
        if ($grid.Columns["Id"]) { $grid.Columns["Id"].Visible = $false }
        $lblInfo.Text = "$($dupGroups.Count) duplicate Device Name(s) across $($displayRows.Count) row(s)."
    }

    $btnGoTo.add_Click({
        if ($grid.SelectedRows.Count -ne 1) {
            [System.Windows.Forms.MessageBox]::Show("Select exactly one row to jump to.", "Go to Selected", "OK", "Information") | Out-Null
            return
        }
        $item = $grid.SelectedRows[0].DataBoundItem
        Show-RowInMainGrid -Id $item["Id"] -StatusKey $item["StatusKey"]
    })

    $btnDelete.add_Click({
        if ($grid.SelectedRows.Count -eq 0) {
            [System.Windows.Forms.MessageBox]::Show("Select one or more rows to delete.", "No Selection", "OK", "Information") | Out-Null
            return
        }
        $ids = @($grid.SelectedRows | Sort-Object Index | ForEach-Object { $_.DataBoundItem["Id"] })
        $confirm = [System.Windows.Forms.MessageBox]::Show("Delete $($ids.Count) selected duplicate row(s)? This cannot be undone from here (though an automatic backup exists).", "Confirm Delete", "YesNo", "Warning")
        if ($confirm -eq [System.Windows.Forms.DialogResult]::Yes) {
            $removed = Remove-InventoryRowsById -Ids $ids
            Write-AppLog "Deleted $removed duplicate device row(s)." "Info"
            & $runDupCheck
        }
    })

    $btnRefresh.add_Click($runDupCheck)
    $dlg.Add_Shown($runDupCheck)

    [void]$dlg.ShowDialog()
    $dlg.Dispose()
}

function Import-AdComputersToInventory([array]$ComputerNames, [string]$TargetStatus) {
    $added = 0
    foreach ($name in $ComputerNames) {
        if ([string]::IsNullOrWhiteSpace($name)) { continue }
        $newRow = $script:Items.NewRow()
        $newRow["Id"] = Get-NextId
        $newRow["Status"] = $TargetStatus
        $newRow["ADFlagReason"] = ""
        $newRow["ADComputerDisabled"] = ""
        foreach ($f in $script:FieldDefs) { $newRow[$f.Key] = Get-FieldDefault $f }
        $newRow["DeviceName"] = "$name".Trim()
        $newRow["Domain"] = "CONTOSO"
        $script:Items.Rows.Add($newRow)
        $added++
    }
    if ($added -gt 0) {
        Save-Inventory
        Refresh-AllTabs
    }
    return $added
}

function Show-ActiveDirectorySyncDialog {
    $dlg = New-Object System.Windows.Forms.Form
    $dlg.Text = "Active Directory Sync"
    $dlg.Size = New-Object System.Drawing.Size(900, 600)
    $dlg.StartPosition = "CenterParent"
    $dlg.MinimumSize = New-Object System.Drawing.Size(700, 400)

    $tabs = New-Object System.Windows.Forms.TabControl
    $tabs.Dock = "Fill"

    $tabDisabled = New-Object System.Windows.Forms.TabPage
    $tabDisabled.Text = "User Disabled"
    $gridDisabled = New-Object System.Windows.Forms.DataGridView
    $gridDisabled.Dock = "Fill"
    $gridDisabled.ReadOnly = $true
    $gridDisabled.AllowUserToAddRows = $false
    $gridDisabled.AutoSizeColumnsMode = "Fill"
    $tabDisabled.Controls.Add($gridDisabled)

    $tabNotFound = New-Object System.Windows.Forms.TabPage
    $tabNotFound.Text = "User Not Found in AD"
    $gridNotFound = New-Object System.Windows.Forms.DataGridView
    $gridNotFound.Dock = "Fill"
    $gridNotFound.ReadOnly = $true
    $gridNotFound.AllowUserToAddRows = $false
    $gridNotFound.AutoSizeColumnsMode = "Fill"
    $tabNotFound.Controls.Add($gridNotFound)

    $tabCompDisabled = New-Object System.Windows.Forms.TabPage
    $tabCompDisabled.Text = "Computer Disabled"
    $gridCompDisabled = New-Object System.Windows.Forms.DataGridView
    $gridCompDisabled.Dock = "Fill"
    $gridCompDisabled.ReadOnly = $true
    $gridCompDisabled.AllowUserToAddRows = $false
    $gridCompDisabled.AutoSizeColumnsMode = "Fill"
    $tabCompDisabled.Controls.Add($gridCompDisabled)

    $tabChanges = New-Object System.Windows.Forms.TabPage
    $tabChanges.Text = "Recent AD Changes (30 days)"
    $gridChanges = New-Object System.Windows.Forms.DataGridView
    $gridChanges.Dock = "Fill"
    $gridChanges.ReadOnly = $true
    $gridChanges.AllowUserToAddRows = $false
    $gridChanges.AutoSizeColumnsMode = "Fill"
    $tabChanges.Controls.Add($gridChanges)

    $tabMissing = New-Object System.Windows.Forms.TabPage
    $tabMissing.Text = "Computers Not in Inventory"

    $importPanel = New-Object System.Windows.Forms.Panel
    $importPanel.Dock = "Top"
    $importPanel.Height = 36

    $lblImportTo = New-Object System.Windows.Forms.Label
    $lblImportTo.Text = "Import selected as:"
    $lblImportTo.Location = New-Object System.Drawing.Point(10, 10)
    $lblImportTo.Size = New-Object System.Drawing.Size(110, 20)
    $importPanel.Controls.Add($lblImportTo)

    $cmbImportTarget = New-Object System.Windows.Forms.ComboBox
    $cmbImportTarget.Location = New-Object System.Drawing.Point(125, 7)
    $cmbImportTarget.Size = New-Object System.Drawing.Size(160, 20)
    $cmbImportTarget.DropDownStyle = "DropDownList"
    foreach ($s in $script:Statuses) { [void]$cmbImportTarget.Items.Add($s.Label) }
    $unknownIndex = $cmbImportTarget.Items.IndexOf("Unknown")
    $cmbImportTarget.SelectedIndex = if ($unknownIndex -ge 0) { $unknownIndex } else { 0 }
    $importPanel.Controls.Add($cmbImportTarget)

    $btnImportMissing = New-Object System.Windows.Forms.Button
    $btnImportMissing.Text = "Import Selected"
    $btnImportMissing.Location = New-Object System.Drawing.Point(295, 5)
    $btnImportMissing.Size = New-Object System.Drawing.Size(120, 26)
    $importPanel.Controls.Add($btnImportMissing)

    $gridMissing = New-Object System.Windows.Forms.DataGridView
    $gridMissing.Dock = "Fill"
    $gridMissing.ReadOnly = $true
    $gridMissing.AllowUserToAddRows = $false
    $gridMissing.MultiSelect = $true
    $gridMissing.SelectionMode = "FullRowSelect"
    $gridMissing.AutoSizeColumnsMode = "Fill"
    $tabMissing.Controls.Add($gridMissing)
    $tabMissing.Controls.Add($importPanel)

    [void]$tabs.TabPages.Add($tabDisabled)
    [void]$tabs.TabPages.Add($tabNotFound)
    [void]$tabs.TabPages.Add($tabCompDisabled)
    [void]$tabs.TabPages.Add($tabChanges)
    [void]$tabs.TabPages.Add($tabMissing)

    $bottomPanel = New-Object System.Windows.Forms.Panel
    $bottomPanel.Dock = "Bottom"
    $bottomPanel.Height = 45

    $lblResult = New-Object System.Windows.Forms.Label
    $lblResult.Text = "Running Active Directory check..."
    $lblResult.Location = New-Object System.Drawing.Point(10, 15)
    $lblResult.Size = New-Object System.Drawing.Size(600, 20)
    $bottomPanel.Controls.Add($lblResult)

    $btnRefresh = New-Object System.Windows.Forms.Button
    $btnRefresh.Text = "Refresh"
    $btnRefresh.Location = New-Object System.Drawing.Point(700, 8)
    $btnRefresh.Size = New-Object System.Drawing.Size(80, 28)
    $bottomPanel.Controls.Add($btnRefresh)

    $btnClose = New-Object System.Windows.Forms.Button
    $btnClose.Text = "Close"
    $btnClose.Location = New-Object System.Drawing.Point(790, 8)
    $btnClose.Size = New-Object System.Drawing.Size(80, 28)
    $btnClose.add_Click({ $dlg.Close() })
    $bottomPanel.Controls.Add($btnClose)

    $dlg.Controls.Add($tabs)
    $dlg.Controls.Add($bottomPanel)

    $runCheck = {
        $lblResult.Text = "Running Active Directory check... (this can take several seconds - the window may look unresponsive, it isn't)"
        $dlg.Cursor = [System.Windows.Forms.Cursors]::WaitCursor
        [System.Windows.Forms.Application]::DoEvents()

        try {
            $matchResult = Invoke-AdUserMatchCheck
            if ($null -ne $matchResult) {
                $disabledRows = @(@($script:Items.Select("[ADFlagReason] = 'AD account disabled'")) | ForEach-Object {
                    [PSCustomObject]@{ DeviceName = $_.DeviceName; LastName = $_.LastName; FirstName = $_.FirstName; Status = $_.Status }
                })
                $gridDisabled.DataSource = [System.Collections.ArrayList]$disabledRows

                $notFoundRows = @(@($script:Items.Select("[ADFlagReason] = 'No matching AD user found'")) | ForEach-Object {
                    [PSCustomObject]@{ DeviceName = $_.DeviceName; LastName = $_.LastName; FirstName = $_.FirstName; Status = $_.Status }
                })
                $gridNotFound.DataSource = [System.Collections.ArrayList]$notFoundRows

                $compDisabledRows = @(@($script:Items.Select("[ADComputerDisabled] <> ''")) | ForEach-Object {
                    [PSCustomObject]@{ DeviceName = $_.DeviceName; LastName = $_.LastName; FirstName = $_.FirstName; Status = $_.Status }
                })
                $gridCompDisabled.DataSource = [System.Collections.ArrayList]$compDisabledRows
            }

            $changes = @(Get-AdRecentUserChanges -Days 30)
            $gridChanges.DataSource = [System.Collections.ArrayList]@($changes)

            $missingComputers = @(Get-AdComputersNotInInventory)
            $gridMissing.DataSource = [System.Collections.ArrayList]@($missingComputers)

            if ($null -ne $matchResult) {
                $lblResult.Text = "Checked against $($matchResult.TotalAdUsers) AD user(s): $($matchResult.ComputerNotFoundCount) computer(s) not in AD ($($matchResult.AutoMovedComputerNotInAdCount) auto-moved to Computer Not in AD), $($matchResult.NotFoundDeviceCount) have no matching AD user ($($matchResult.AutoMovedUserNotInAdCount) auto-moved to User Not in AD), $($matchResult.DisabledDeviceCount) belong to a disabled account, $($matchResult.ComputerDisabledCount) have a disabled computer object ($($matchResult.AutoMovedCount) auto-moved to Disabled Devices). $($changes.Count) recent AD change(s). $($missingComputers.Count) AD computer(s) not in inventory."
                if ($matchResult.DisabledDeviceCount -gt 0) { Write-AppLog "AD check: $($matchResult.DisabledDeviceCount) device(s) belong to a disabled AD account." "Warning" }
                if ($matchResult.NotFoundDeviceCount -gt 0) { Write-AppLog "AD check: $($matchResult.NotFoundDeviceCount) device(s) have NO matching AD user at all." "Error" }
                if ($matchResult.ComputerNotFoundCount -gt 0) { Write-AppLog "AD check: $($matchResult.ComputerNotFoundCount) device(s) have NO matching AD computer object at all." "Error" }
                if ($matchResult.AutoMovedComputerNotInAdCount -gt 0) { Write-AppLog "AD check: auto-moved $($matchResult.AutoMovedComputerNotInAdCount) device(s) to Computer Not in AD (no matching AD computer object found)." "Error" }
                if ($matchResult.AutoMovedUserNotInAdCount -gt 0) { Write-AppLog "AD check: auto-moved $($matchResult.AutoMovedUserNotInAdCount) device(s) to User Not in AD (no matching AD user found)." "Error" }
                if ($matchResult.AutoMovedCount -gt 0) { Write-AppLog "AD check: auto-moved $($matchResult.AutoMovedCount) device(s) to Disabled Devices (computer object disabled in AD)." "Warning" }
            } else {
                $lblResult.Text = "Active Directory check could not complete - see error dialog."
            }
        } finally {
            $dlg.Cursor = [System.Windows.Forms.Cursors]::Default
        }
    }

    $btnImportMissing.add_Click({
        if ($gridMissing.SelectedRows.Count -eq 0) {
            [System.Windows.Forms.MessageBox]::Show("Select one or more computers to import.", "No Selection", "OK", "Information") | Out-Null
            return
        }
        $targetLabel = "$($cmbImportTarget.SelectedItem)"
        $targetStatus = ($script:Statuses | Where-Object { $_.Label -eq $targetLabel }).Key
        $names = @($gridMissing.SelectedRows | ForEach-Object { $_.DataBoundItem.ComputerName })
        $confirm = [System.Windows.Forms.MessageBox]::Show("Import $($names.Count) computer(s) into '$targetLabel'?", "Confirm Import", "YesNo", "Question")
        if ($confirm -eq [System.Windows.Forms.DialogResult]::Yes) {
            $added = Import-AdComputersToInventory -ComputerNames $names -TargetStatus $targetStatus
            [System.Windows.Forms.MessageBox]::Show("Imported $added computer(s) into '$targetLabel'.", "Import Complete", "OK", "Information") | Out-Null
            & $runCheck
        }
    })

    $btnRefresh.add_Click($runCheck)
    $dlg.Add_Shown($runCheck)

    [void]$dlg.ShowDialog()
    $dlg.Dispose()
}

function New-InventoryTab {
    param(
        [string]$StatusKey,
        [string]$StatusLabel,
        [array]$MoveTargets,
        [string]$RowFilterOverride = $null,
        [switch]$HideCreateButtons,
        [hashtable]$DomainMoveTarget = $null
    )

    # Off-Domain devices are excluded from every real status tab's base filter (they only ever
    # show up in the Off Domain view now) - a tab built with a RowFilterOverride (currently just
    # Off Domain itself) is exempt, since that override IS the Off-Domain filter.
    $script:BaseFilters[$StatusKey] = if ($RowFilterOverride) { $RowFilterOverride } else { "[Status] = '$StatusKey' AND [Domain] <> 'Off-Domain'" }

    $tabPage = New-Object System.Windows.Forms.TabPage
    $tabPage.Text = $StatusLabel

    # Top panel: search + status label
    $topPanel = New-Object System.Windows.Forms.Panel
    $topPanel.Dock = "Top"
    $topPanel.Height = 40
    $tabPage.Controls.Add($topPanel)

    $lblSearch = New-Object System.Windows.Forms.Label
    $lblSearch.Text = "Search:"
    $lblSearch.Location = New-Object System.Drawing.Point(10, 12)
    $lblSearch.Size = New-Object System.Drawing.Size(50, 20)
    $topPanel.Controls.Add($lblSearch)

    $txtSearch = New-Object System.Windows.Forms.TextBox
    $txtSearch.Location = New-Object System.Drawing.Point(65, 9)
    $txtSearch.Size = New-Object System.Drawing.Size(300, 20)
    $topPanel.Controls.Add($txtSearch)
    $script:SearchBoxes[$StatusKey] = $txtSearch

    $btnClearSearch = New-Object System.Windows.Forms.Button
    $btnClearSearch.Text = [char]0x2715
    $btnClearSearch.Location = New-Object System.Drawing.Point(368, 8)
    $btnClearSearch.Size = New-Object System.Drawing.Size(24, 22)
    # .GetNewClosure() is required here: a plain {} scriptblock does NOT capture this
    # function's local variables the way a real closure would - it resolves them by walking the
    # scope chain at the moment the event actually fires, and by then New-InventoryTab has long
    # since returned, so $txtSearch would resolve to $null (confirmed by reproduction: this
    # exact bug was live in production, throwing "You cannot call a method on a null-valued
    # expression" the instant a user clicked the clear button on any tab).
    $btnClearSearch.add_Click({ $txtSearch.Clear() }.GetNewClosure())
    $topPanel.Controls.Add($btnClearSearch)

    $lblStatus = New-Object System.Windows.Forms.Label
    $lblStatus.Location = New-Object System.Drawing.Point(400, 12)
    $lblStatus.Size = New-Object System.Drawing.Size(850, 20)
    $lblStatus.ForeColor = [System.Drawing.Color]::DarkRed
    $topPanel.Controls.Add($lblStatus)
    $script:StatusLabels[$StatusKey] = $lblStatus

    # Grid
    $grid = New-Object System.Windows.Forms.DataGridView
    $grid.Dock = "Fill"
    $grid.ReadOnly = $true
    $grid.AllowUserToAddRows = $false
    $grid.AllowUserToDeleteRows = $false
    $grid.SelectionMode = "FullRowSelect"
    $grid.MultiSelect = $true
    $grid.AutoSizeColumnsMode = "None"
    $grid.RowHeadersVisible = $false
    $grid.AllowUserToResizeRows = $false
    $grid.Tag = $StatusKey

    $doubleBufferProp = [System.Windows.Forms.DataGridView].GetProperty("DoubleBuffered", [System.Reflection.BindingFlags]"Instance, NonPublic")
    $doubleBufferProp.SetValue($grid, $true, $null)

    $view = New-Object System.Data.DataView($script:Items)
    $grid.DataSource = $view
    $script:Grids[$StatusKey] = $grid
    $script:Views[$StatusKey] = $view
    $script:SerialColIndex[$StatusKey] = -1
    $script:ADFlagColIndex[$StatusKey] = -1
    $script:ADComputerColIndex[$StatusKey] = -1
    $script:ADComputerNotFoundColIndex[$StatusKey] = -1
    $script:IgnoreHighlightColIndex[$StatusKey] = -1
    $script:DeviceNameColIndex[$StatusKey] = -1
    $script:GroupPrimaryColIndex[$StatusKey] = -1
    $script:GroupCountColIndex[$StatusKey] = -1
    $script:StatusColIndex[$StatusKey] = -1
    $script:ContextMenuValidRow[$StatusKey] = $false

    $grid.add_DataBindingComplete({
        param($sender, $e)
        $key = $sender.Tag
        if ($sender.Columns["Id"]) { $sender.Columns["Id"].Visible = $false }
        if ($sender.Columns["Status"]) { $sender.Columns["Status"].Visible = $false }
        if ($sender.Columns["ADFlagReason"]) { $sender.Columns["ADFlagReason"].Visible = $false }
        if ($sender.Columns["ADComputerDisabled"]) { $sender.Columns["ADComputerDisabled"].Visible = $false }
        if ($sender.Columns["ADComputerNotFound"]) { $sender.Columns["ADComputerNotFound"].Visible = $false }
        if ($sender.Columns["IgnoreHighlight"]) { $sender.Columns["IgnoreHighlight"].Visible = $false }
        foreach ($f in $script:FieldDefs) {
            if ($sender.Columns[$f.Key]) {
                $sender.Columns[$f.Key].HeaderText = $f.Label
                if ($f.ContainsKey("Hidden") -and $f.Hidden) { $sender.Columns[$f.Key].Visible = $false }
            }
        }
        if ($sender.Columns["GroupCollapsed"]) { $sender.Columns["GroupCollapsed"].Visible = $false }
        if ($sender.Columns["GroupPrimary"]) { $sender.Columns["GroupPrimary"].Visible = $false }
        if ($sender.Columns["GroupCount"]) { $sender.Columns["GroupCount"].Visible = $false }
        if ($sender.Columns["SerialNumber"]) { $script:SerialColIndex[$key] = $sender.Columns["SerialNumber"].Index }
        if ($sender.Columns["ADFlagReason"]) { $script:ADFlagColIndex[$key] = $sender.Columns["ADFlagReason"].Index }
        if ($sender.Columns["ADComputerDisabled"]) { $script:ADComputerColIndex[$key] = $sender.Columns["ADComputerDisabled"].Index }
        if ($sender.Columns["ADComputerNotFound"]) { $script:ADComputerNotFoundColIndex[$key] = $sender.Columns["ADComputerNotFound"].Index }
        if ($sender.Columns["IgnoreHighlight"]) { $script:IgnoreHighlightColIndex[$key] = $sender.Columns["IgnoreHighlight"].Index }
        if ($sender.Columns["DeviceName"]) { $script:DeviceNameColIndex[$key] = $sender.Columns["DeviceName"].Index }
        if ($sender.Columns["GroupPrimary"]) { $script:GroupPrimaryColIndex[$key] = $sender.Columns["GroupPrimary"].Index }
        if ($sender.Columns["GroupCount"]) { $script:GroupCountColIndex[$key] = $sender.Columns["GroupCount"].Index }
        if ($sender.Columns["Status"]) { $script:StatusColIndex[$key] = $sender.Columns["Status"].Index }
    })

    $grid.add_CellFormatting({
        param($sender, $e)
        try {
            $deviceColIndex = $script:DeviceNameColIndex[$sender.Tag]
            $groupCountColIndex = $script:GroupCountColIndex[$sender.Tag]
            $groupPrimaryColIndex = $script:GroupPrimaryColIndex[$sender.Tag]
            $statusColIndex = $script:StatusColIndex[$sender.Tag]
            if ($deviceColIndex -ge 0 -and $e.ColumnIndex -eq $deviceColIndex -and $groupCountColIndex -ge 0 -and $groupPrimaryColIndex -ge 0 -and $statusColIndex -ge 0) {
                $groupCount = "$($sender.Rows[$e.RowIndex].Cells[$groupCountColIndex].Value)"
                if (-not [string]::IsNullOrWhiteSpace($groupCount)) {
                    $isPrimary = "$($sender.Rows[$e.RowIndex].Cells[$groupPrimaryColIndex].Value)" -eq "Yes"
                    if ($isPrimary) {
                        $statusVal = "$($sender.Rows[$e.RowIndex].Cells[$statusColIndex].Value)".Trim().ToLowerInvariant()
                        $deviceKey = $statusVal + "|" + "$($e.Value)".Trim().ToLowerInvariant()
                        $expanded = $script:ExpandedGroups.ContainsKey($deviceKey) -and $script:ExpandedGroups[$deviceKey]
                        $extra = [int]$groupCount - 1
                        $noun = if ($extra -eq 1) { "user" } else { "users" }
                        $arrow = if ($expanded) { [char]0x25BC } else { [char]0x25B6 }
                        $e.Value = "$arrow $($e.Value)  (+$extra other $noun)"
                    } else {
                        $e.Value = "      " + [char]0x21B3 + " $($e.Value)"
                    }
                    $e.FormattingApplied = $true
                }
            }
        } catch {}
        try {
            $ignoreColIndex = $script:IgnoreHighlightColIndex[$sender.Tag]
            if ($ignoreColIndex -ge 0) {
                $ignoreFlag = "$($sender.Rows[$e.RowIndex].Cells[$ignoreColIndex].Value)"
                if (-not [string]::IsNullOrWhiteSpace($ignoreFlag)) { return }
            }
            $adCompColIndex = $script:ADComputerColIndex[$sender.Tag]
            if ($adCompColIndex -ge 0) {
                $adCompFlag = "$($sender.Rows[$e.RowIndex].Cells[$adCompColIndex].Value)"
                if (-not [string]::IsNullOrWhiteSpace($adCompFlag)) {
                    $e.CellStyle.BackColor = [System.Drawing.Color]::FromArgb(200, 200, 200)
                    return
                }
            }
            $adCompNotFoundColIndex = $script:ADComputerNotFoundColIndex[$sender.Tag]
            if ($adCompNotFoundColIndex -ge 0) {
                $adCompNotFoundFlag = "$($sender.Rows[$e.RowIndex].Cells[$adCompNotFoundColIndex].Value)"
                if (-not [string]::IsNullOrWhiteSpace($adCompNotFoundFlag)) {
                    $e.CellStyle.BackColor = [System.Drawing.Color]::FromArgb(216, 191, 255)
                    return
                }
            }
            $adColIndex = $script:ADFlagColIndex[$sender.Tag]
            if ($adColIndex -ge 0) {
                $adFlag = "$($sender.Rows[$e.RowIndex].Cells[$adColIndex].Value)"
                if ($adFlag -eq "No matching AD user found") {
                    $e.CellStyle.BackColor = [System.Drawing.Color]::FromArgb(255, 150, 150)
                    return
                }
                if (-not [string]::IsNullOrWhiteSpace($adFlag)) {
                    $e.CellStyle.BackColor = [System.Drawing.Color]::FromArgb(255, 224, 178)
                    return
                }
            }
            $colIndex = $script:SerialColIndex[$sender.Tag]
            if ($colIndex -lt 0) { return }
            $serial = $sender.Rows[$e.RowIndex].Cells[$colIndex].Value
            if ([string]::IsNullOrWhiteSpace($serial)) {
                $e.CellStyle.BackColor = [System.Drawing.Color]::MistyRose
            }
        } catch {}
    })

    $grid.add_CellDoubleClick({
        param($sender, $e)
        if ($e.RowIndex -ge 0) { Edit-Item -StatusKey $sender.Tag }
    })

    $grid.add_CellClick({
        param($sender, $e)
        if ($e.RowIndex -lt 0) { return }
        $deviceColIndex = $script:DeviceNameColIndex[$sender.Tag]
        $groupPrimaryColIndex = $script:GroupPrimaryColIndex[$sender.Tag]
        $statusColIndex = $script:StatusColIndex[$sender.Tag]
        if ($deviceColIndex -lt 0 -or $groupPrimaryColIndex -lt 0 -or $statusColIndex -lt 0 -or $e.ColumnIndex -ne $deviceColIndex) { return }
        $row = $sender.Rows[$e.RowIndex]
        if ("$($row.Cells[$groupPrimaryColIndex].Value)" -ne "Yes") { return }
        $statusVal = "$($row.Cells[$statusColIndex].Value)".Trim().ToLowerInvariant()
        $deviceKey = $statusVal + "|" + "$($row.Cells[$deviceColIndex].Value)".Trim().ToLowerInvariant()
        Toggle-DeviceGroup -DeviceKey $deviceKey
    })

    $grid.add_MouseDown({
        param($sender, $e)
        if ($e.Button -eq [System.Windows.Forms.MouseButtons]::Right) {
            $hit = $sender.HitTest($e.X, $e.Y)
            if ($hit.RowIndex -ge 0) {
                if (-not $sender.Rows[$hit.RowIndex].Selected) {
                    $sender.ClearSelection()
                    $sender.Rows[$hit.RowIndex].Selected = $true
                }
                $colIndex = $hit.ColumnIndex
                if ($colIndex -lt 0 -or -not $sender.Columns[$colIndex].Visible) {
                    $firstVisible = $sender.Columns | Where-Object { $_.Visible } | Select-Object -First 1
                    $colIndex = $firstVisible.Index
                }
                $sender.CurrentCell = $sender.Rows[$hit.RowIndex].Cells[$colIndex]
                $script:ContextMenuValidRow[$sender.Tag] = $true
            } else {
                $script:ContextMenuValidRow[$sender.Tag] = $false
            }
        }
    })

    $txtSearch.add_TextChanged([ScriptBlock]::Create("Apply-Filter -StatusKey '$StatusKey'"))

    $tabPage.Controls.Add($grid)
    $tabPage.Controls.Add($topPanel)
    $grid.BringToFront()

    # Bottom panel: buttons
    $bottomPanel = New-Object System.Windows.Forms.Panel
    $bottomPanel.Dock = "Bottom"
    $bottomPanel.Height = 45
    $tabPage.Controls.Add($bottomPanel)

    $x = 10
    if (-not $HideCreateButtons) {
        $btnAdd = New-Object System.Windows.Forms.Button
        $btnAdd.Text = "Add Item"
        $btnAdd.Location = New-Object System.Drawing.Point($x, 8)
        $btnAdd.Size = New-Object System.Drawing.Size(90, 28)
        $btnAdd.add_Click([ScriptBlock]::Create("Add-Item -StatusKey '$StatusKey'"))
        $bottomPanel.Controls.Add($btnAdd)
        $x += 100
    }

    $btnEdit = New-Object System.Windows.Forms.Button
    $btnEdit.Text = "Edit Item"
    $btnEdit.Location = New-Object System.Drawing.Point($x, 8)
    $btnEdit.Size = New-Object System.Drawing.Size(90, 28)
    $btnEdit.add_Click([ScriptBlock]::Create("Edit-Item -StatusKey '$StatusKey'"))
    $bottomPanel.Controls.Add($btnEdit)
    $x += 100

    $btnDelete = New-Object System.Windows.Forms.Button
    $btnDelete.Text = "Delete Item"
    $btnDelete.Location = New-Object System.Drawing.Point($x, 8)
    $btnDelete.Size = New-Object System.Drawing.Size(90, 28)
    $btnDelete.add_Click([ScriptBlock]::Create("Delete-Items -StatusKey '$StatusKey'"))
    $bottomPanel.Controls.Add($btnDelete)
    $x += 100

    $lblMoveTo = New-Object System.Windows.Forms.Label
    $lblMoveTo.Text = "Move to:"
    $lblMoveTo.Location = New-Object System.Drawing.Point($x, 13)
    $lblMoveTo.Size = New-Object System.Drawing.Size(55, 20)
    $bottomPanel.Controls.Add($lblMoveTo)
    $x += 58

    $cmbMoveTarget = New-Object System.Windows.Forms.ComboBox
    $cmbMoveTarget.Location = New-Object System.Drawing.Point($x, 10)
    $cmbMoveTarget.Size = New-Object System.Drawing.Size(160, 20)
    $cmbMoveTarget.DropDownStyle = "DropDownList"
    foreach ($target in $MoveTargets) { [void]$cmbMoveTarget.Items.Add($target.Label) }
    if ($DomainMoveTarget) { [void]$cmbMoveTarget.Items.Add($DomainMoveTarget.Label) }
    if ($cmbMoveTarget.Items.Count -gt 0) { $cmbMoveTarget.SelectedIndex = 0 }
    $bottomPanel.Controls.Add($cmbMoveTarget)
    $x += 165

    $btnMove = New-Object System.Windows.Forms.Button
    $btnMove.Text = "Move"
    $btnMove.Location = New-Object System.Drawing.Point($x, 7)
    $btnMove.Size = New-Object System.Drawing.Size(70, 28)
    # Same .GetNewClosure() requirement as the clear-search button above - this handler
    # references $cmbMoveTarget, $DomainMoveTarget, and $StatusKey, all local to this function
    # call, which would otherwise resolve to $null once the button is actually clicked (long
    # after New-InventoryTab has returned). GetNewClosure() alone isn't quite enough here,
    # though: the target-status lookup is routed through Get-StatusKeyForLabel instead of an
    # inline "$script:Statuses | Where-Object ..." - a direct $script: reference written INSIDE
    # a GetNewClosure()'d body resolves against that closure's own isolated snapshot rather than
    # the real running script, silently returning nothing instead of erroring. Before this fix,
    # clicking Move fell through to a "Move 'X' to ?" prompt with a blank destination instead of
    # an error, since nothing here calls a .NET method directly on a null value the way the
    # clear-search bug did.
    $btnMove.add_Click({
        if ($cmbMoveTarget.SelectedIndex -lt 0) {
            [System.Windows.Forms.MessageBox]::Show("Choose a destination tab first.", "Move", "OK", "Information") | Out-Null
            return
        }
        $targetLabel = "$($cmbMoveTarget.SelectedItem)"
        if ($DomainMoveTarget -and $targetLabel -eq $DomainMoveTarget.Label) {
            Set-DeviceDomain -StatusKey $StatusKey -DomainValue $DomainMoveTarget.Value -DomainLabel $DomainMoveTarget.Label
        } else {
            $targetKey = Get-StatusKeyForLabel -Label $targetLabel
            Move-SelectedItems -FromStatus $StatusKey -ToStatus $targetKey
        }
    }.GetNewClosure())
    $bottomPanel.Controls.Add($btnMove)
    $x += 80

    if (-not $HideCreateButtons) {
        $btnImport = New-Object System.Windows.Forms.Button
        $btnImport.Text = "Import CSV"
        $btnImport.Location = New-Object System.Drawing.Point($x, 8)
        $btnImport.Size = New-Object System.Drawing.Size(90, 28)
        $btnImport.add_Click([ScriptBlock]::Create("Import-ItemsCsv -StatusKey '$StatusKey'"))
        $bottomPanel.Controls.Add($btnImport)
        $x += 100
    }

    $btnExport = New-Object System.Windows.Forms.Button
    $btnExport.Text = "Export CSV"
    $btnExport.Location = New-Object System.Drawing.Point($x, 8)
    $btnExport.Size = New-Object System.Drawing.Size(90, 28)
    $btnExport.add_Click([ScriptBlock]::Create("Export-ItemsCsv -StatusKey '$StatusKey'"))
    $bottomPanel.Controls.Add($btnExport)
    $x += 100

    $lblLegend = New-Object System.Windows.Forms.Label
    $lblLegend.Text = "Pink=NoSerial  Orange=AcctDisabled  Red=UserNotInAD  Purple=CompNotInAD  Grey=CompOff"
    $lblLegend.Location = New-Object System.Drawing.Point($x, 14)
    $lblLegend.AutoSize = $true
    $lblLegend.ForeColor = [System.Drawing.Color]::Gray
    $bottomPanel.Controls.Add($lblLegend)

    # Context menu: Edit, Delete, Move to each other tab
    $gridContextMenu = New-Object System.Windows.Forms.ContextMenuStrip
    $menuEdit = New-Object System.Windows.Forms.ToolStripMenuItem
    $menuEdit.Text = "Edit"
    $menuEdit.add_Click([ScriptBlock]::Create("Edit-Item -StatusKey '$StatusKey'"))
    [void]$gridContextMenu.Items.Add($menuEdit)

    $menuDelete = New-Object System.Windows.Forms.ToolStripMenuItem
    $menuDelete.Text = "Delete"
    $menuDelete.add_Click([ScriptBlock]::Create("Delete-Items -StatusKey '$StatusKey'"))
    [void]$gridContextMenu.Items.Add($menuDelete)

    [void]$gridContextMenu.Items.Add((New-Object System.Windows.Forms.ToolStripSeparator))

    foreach ($target in $MoveTargets) {
        $menuMove = New-Object System.Windows.Forms.ToolStripMenuItem
        $menuMove.Text = "Move to $($target.Label)"
        $menuMove.add_Click([ScriptBlock]::Create("Move-SelectedItems -FromStatus '$StatusKey' -ToStatus '$($target.Key)'"))
        [void]$gridContextMenu.Items.Add($menuMove)
    }

    if ($DomainMoveTarget) {
        $menuMoveDomain = New-Object System.Windows.Forms.ToolStripMenuItem
        $menuMoveDomain.Text = "Move to $($DomainMoveTarget.Label)"
        $menuMoveDomain.add_Click([ScriptBlock]::Create("Set-DeviceDomain -StatusKey '$StatusKey' -DomainValue '$($DomainMoveTarget.Value)' -DomainLabel '$($DomainMoveTarget.Label)'"))
        [void]$gridContextMenu.Items.Add($menuMoveDomain)
    }

    [void]$gridContextMenu.Items.Add((New-Object System.Windows.Forms.ToolStripSeparator))

    $menuPullDescription = New-Object System.Windows.Forms.ToolStripMenuItem
    $menuPullDescription.Text = "Pull Description from AD"
    $menuPullDescription.add_Click([ScriptBlock]::Create("Pull-AdDescriptionsForSelected -StatusKey '$StatusKey'"))
    [void]$gridContextMenu.Items.Add($menuPullDescription)

    $menuCheckAd = New-Object System.Windows.Forms.ToolStripMenuItem
    $menuCheckAd.Text = "Check AD"
    $menuCheckAd.add_Click([ScriptBlock]::Create("Invoke-AdCheckForSelected -StatusKey '$StatusKey'"))
    [void]$gridContextMenu.Items.Add($menuCheckAd)

    $menuMatchUser = New-Object System.Windows.Forms.ToolStripMenuItem
    $menuMatchUser.Text = "Match User"
    $menuMatchUser.add_Click([ScriptBlock]::Create("Invoke-MatchUserToAd -StatusKey '$StatusKey'"))
    [void]$gridContextMenu.Items.Add($menuMatchUser)

    $menuClearUsername = New-Object System.Windows.Forms.ToolStripMenuItem
    $menuClearUsername.Text = "Clear Username"
    $menuClearUsername.add_Click([ScriptBlock]::Create("Clear-Username -StatusKey '$StatusKey'"))
    [void]$gridContextMenu.Items.Add($menuClearUsername)

    $menuTransferUser = New-Object System.Windows.Forms.ToolStripMenuItem
    $menuTransferUser.Text = "Transfer User"
    $menuTransferUser.add_Click([ScriptBlock]::Create("Invoke-TransferUser -StatusKey '$StatusKey'"))
    [void]$gridContextMenu.Items.Add($menuTransferUser)

    $menuOffboardUser = New-Object System.Windows.Forms.ToolStripMenuItem
    $menuOffboardUser.Text = "Offboard User"
    $menuOffboardUser.add_Click([ScriptBlock]::Create("Invoke-OffboardUser -StatusKey '$StatusKey'"))
    [void]$gridContextMenu.Items.Add($menuOffboardUser)

    [void]$gridContextMenu.Items.Add((New-Object System.Windows.Forms.ToolStripSeparator))

    # One toggle instead of two opposite fixed-action items - label reflects the FIRST selected
    # row's current state (computed in add_Opening below, same pattern as Edit/Delete's own
    # selection-count relabeling) and clicking it flips ALL selected rows to the opposite state.
    $menuToggleHighlight = New-Object System.Windows.Forms.ToolStripMenuItem
    $menuToggleHighlight.Text = "Ignore Highlight Color"
    $menuToggleHighlight.Tag = "ToggleHighlight"
    $menuToggleHighlight.add_Click([ScriptBlock]::Create("Invoke-ToggleHighlight -StatusKey '$StatusKey'"))
    [void]$gridContextMenu.Items.Add($menuToggleHighlight)

    $gridContextMenu.Tag = $StatusKey
    $gridContextMenu.add_Opening({
        param($sender, $e)
        $key = $sender.Tag
        if (-not $script:ContextMenuValidRow[$key]) { $e.Cancel = $true; return }
        $grid = $script:Grids[$key]
        $count = $grid.SelectedRows.Count
        $menuItems = $sender.Items
        $menuItems[0].Text = if ($count -gt 1) { "Edit ($count)" } else { "Edit" }
        $menuItems[1].Text = if ($count -gt 1) { "Delete ($count)" } else { "Delete" }
        $toggleItem = $menuItems | Where-Object { $_.Tag -eq "ToggleHighlight" } | Select-Object -First 1
        if ($toggleItem -and $count -gt 0) {
            $firstRow = $script:Items.Rows.Find($grid.SelectedRows[0].DataBoundItem["Id"])
            $currentlyIgnored = ($null -ne $firstRow) -and ("$($firstRow.IgnoreHighlight)" -eq "Yes")
            $toggleItem.Text = if ($currentlyIgnored) { "Show Highlight Color" } else { "Ignore Highlight Color" }
        }
    })

    $grid.ContextMenuStrip = $gridContextMenu

    return $tabPage
}

# ---- Main Form ----
$form = New-Object System.Windows.Forms.Form
$form.Text = "Inventory Manager"
if ($script:AppIcon) { $form.Icon = $script:AppIcon }
$form.Size = New-Object System.Drawing.Size(1450, 650)
$form.StartPosition = "CenterScreen"
$form.MinimumSize = New-Object System.Drawing.Size(900, 400)
$form.WindowState = [System.Windows.Forms.FormWindowState]::Maximized

# The compiled exe's file icon (set via ps2exe -iconFile) doesn't automatically become the
# running window's icon - WinForms only shows Form.Icon in the taskbar/title bar. Extracting
# it from the exe itself keeps the window icon in sync with whatever ps2exe embedded, with
# no separate icon file to ship or keep up to date at runtime.
try {
    $form.Icon = [System.Drawing.Icon]::ExtractAssociatedIcon($script:ExePath)
} catch {}

$menuStrip = New-Object System.Windows.Forms.MenuStrip
$menuStrip.Dock = "Top"
$fileMenu = New-Object System.Windows.Forms.ToolStripMenuItem
$fileMenu.Text = "File"

$menuRestore = New-Object System.Windows.Forms.ToolStripMenuItem
$menuRestore.Text = "Restore from Backup..."
$menuRestore.add_Click({ Show-RestoreBackupDialog })
[void]$fileMenu.DropDownItems.Add($menuRestore)

$menuOpenBackups = New-Object System.Windows.Forms.ToolStripMenuItem
$menuOpenBackups.Text = "Open Backups Folder"
$menuOpenBackups.add_Click({ Open-BackupsFolder })
[void]$fileMenu.DropDownItems.Add($menuOpenBackups)

[void]$fileMenu.DropDownItems.Add((New-Object System.Windows.Forms.ToolStripSeparator))

$menuAdSync = New-Object System.Windows.Forms.ToolStripMenuItem
$menuAdSync.Text = "Active Directory Sync..."
$menuAdSync.add_Click({ Show-ActiveDirectorySyncDialog })
[void]$fileMenu.DropDownItems.Add($menuAdSync)

$menuSearchAd = New-Object System.Windows.Forms.ToolStripMenuItem
$menuSearchAd.Text = "Search Active Directory..."
$menuSearchAd.add_Click({ Show-AdUserSearchDialog })
[void]$fileMenu.DropDownItems.Add($menuSearchAd)

$menuDuplicates = New-Object System.Windows.Forms.ToolStripMenuItem
$menuDuplicates.Text = "Find Duplicate Devices..."
$menuDuplicates.add_Click({ Show-DuplicateDevicesDialog })
[void]$fileMenu.DropDownItems.Add($menuDuplicates)

[void]$fileMenu.DropDownItems.Add((New-Object System.Windows.Forms.ToolStripSeparator))

$menuOnboard = New-Object System.Windows.Forms.ToolStripMenuItem
$menuOnboard.Text = "Onboard New User..."
$menuOnboard.add_Click({ Show-OnboardingWizard })
[void]$fileMenu.DropDownItems.Add($menuOnboard)

$menuSetAdCred = New-Object System.Windows.Forms.ToolStripMenuItem
$menuSetAdCred.Text = "Run AD Actions As..."
$menuSetAdCred.add_Click({ Show-AdCredentialDialog })
[void]$fileMenu.DropDownItems.Add($menuSetAdCred)

$menuClearAdCred = New-Object System.Windows.Forms.ToolStripMenuItem
$menuClearAdCred.Text = "Use My Windows Session for AD"
$menuClearAdCred.add_Click({ Clear-AdCredential })
[void]$fileMenu.DropDownItems.Add($menuClearAdCred)

$fileMenu.add_DropDownOpening({
    if ($null -ne $script:AdCredential) {
        $menuSetAdCred.Text = "Run AD Actions As... (currently: $($script:AdCredential.UserName))"
        $menuClearAdCred.Enabled = $true
    } else {
        $menuSetAdCred.Text = "Run AD Actions As..."
        $menuClearAdCred.Enabled = $false
    }
})

[void]$menuStrip.Items.Add($fileMenu)

$script:TabControl = New-Object System.Windows.Forms.TabControl
$tabControl = $script:TabControl
$tabControl.Dock = "Fill"

# Activity log side panel - built the same way as everything else here (inner Fill added
# before the inner Top/Bottom controls), for the same docking-order reason noted below.
$logPanel = New-Object System.Windows.Forms.Panel
$logPanel.Dock = "Right"
$logPanel.Width = 280

$script:LogListBox = New-Object System.Windows.Forms.ListBox
$script:LogListBox.Dock = "Fill"
$script:LogListBox.Font = New-Object System.Drawing.Font("Consolas", 8)
$script:LogListBox.HorizontalScrollbar = $true
$script:LogListBox.IntegralHeight = $false

$btnClearLog = New-Object System.Windows.Forms.Button
$btnClearLog.Text = "Clear Log"
$btnClearLog.Dock = "Bottom"
$btnClearLog.Height = 28
$btnClearLog.add_Click({ $script:LogListBox.Items.Clear() })

# "Clear Log" above only clears the on-screen list - the underlying file (every session, every
# day, never cleared automatically) is what actually gets handed over when something needs
# troubleshooting, so this is the quickest way to reach it.
$btnOpenLogFolder = New-Object System.Windows.Forms.Button
$btnOpenLogFolder.Text = "Open Logs Folder"
$btnOpenLogFolder.Dock = "Bottom"
$btnOpenLogFolder.Height = 28
$btnOpenLogFolder.add_Click({
    try {
        if (-not (Test-Path $script:LogDir)) { New-Item -Path $script:LogDir -ItemType Directory -Force | Out-Null }
        Start-Process -FilePath "explorer.exe" -ArgumentList "`"$($script:LogDir)`""
    } catch {
        [System.Windows.Forms.MessageBox]::Show("Could not open the logs folder: $($_.Exception.Message)", "Open Logs Folder", "OK", "Error") | Out-Null
    }
})

# Lives here (always-visible log panel) rather than on each tab's own button row, since
# onboarding doesn't act on whatever's selected in the current tab - one button, visible
# regardless of which tab you're on, instead of seven duplicate copies.
$btnOnboardNewUser = New-Object System.Windows.Forms.Button
$btnOnboardNewUser.Text = "Onboard New User"
$btnOnboardNewUser.Dock = "Bottom"
$btnOnboardNewUser.Height = 28
$btnOnboardNewUser.add_Click({ Show-OnboardingWizard })

$logHeaderPanel = New-Object System.Windows.Forms.Panel
$logHeaderPanel.Dock = "Top"
$logHeaderPanel.Height = 26

$lblLogHeader = New-Object System.Windows.Forms.Label
$lblLogHeader.Text = "Activity Log"
$lblLogHeader.Dock = "Fill"
$lblLogHeader.TextAlign = "MiddleLeft"
$lblLogHeader.Padding = New-Object System.Windows.Forms.Padding(6, 0, 0, 0)
$lblLogHeader.Font = New-Object System.Drawing.Font($lblLogHeader.Font, [System.Drawing.FontStyle]::Bold)

$btnToggleLog = New-Object System.Windows.Forms.Button
$btnToggleLog.Text = [char]0x25B6
$btnToggleLog.Dock = "Right"
$btnToggleLog.Width = 26
$btnToggleLog.FlatStyle = "Flat"

# Inner Fill (label) added before the inner Right (button) - same docking-order rule as
# everywhere else in this file.
$logHeaderPanel.Controls.Add($lblLogHeader)
$logHeaderPanel.Controls.Add($btnToggleLog)

$logPanel.Controls.Add($script:LogListBox)
$logPanel.Controls.Add($btnClearLog)
$logPanel.Controls.Add($btnOpenLogFolder)
$logPanel.Controls.Add($btnOnboardNewUser)
$logPanel.Controls.Add($logHeaderPanel)

$script:LogExpandedWidth = 280
$script:LogCollapsed = $false
$toggleLogPanel = {
    $script:LogCollapsed = -not $script:LogCollapsed
    if ($script:LogCollapsed) {
        $logPanel.Width = 26
        $lblLogHeader.Visible = $false
        $script:LogListBox.Visible = $false
        $btnClearLog.Visible = $false
        $btnToggleLog.Text = [char]0x25C0
    } else {
        $logPanel.Width = $script:LogExpandedWidth
        $lblLogHeader.Visible = $true
        $script:LogListBox.Visible = $true
        $btnClearLog.Visible = $true
        $btnToggleLog.Text = [char]0x25B6
    }
}
$btnToggleLog.add_Click($toggleLogPanel)

# The Fill control must be added to Form.Controls BEFORE the Right-docked log panel and the
# Top-docked MenuStrip, or WinForms lets an earlier-added Dock claim space first and later
# ones just overlap instead of the three properly stacking (confirmed empirically - this is
# what was hiding the tab strip underneath the menu bar in an earlier version: add order is
# Fill, then Right/Left, then Top/Bottom, last-added wins its docked slice first).
$form.Controls.Add($tabControl)
$form.Controls.Add($logPanel)
$form.Controls.Add($menuStrip)
$form.MainMenuStrip = $menuStrip

foreach ($s in $script:Statuses) {
    $others = $script:Statuses | Where-Object { $_.Key -ne $s.Key }
    $tabPage = New-InventoryTab -StatusKey $s.Key -StatusLabel $s.Label -MoveTargets $others -DomainMoveTarget @{ Label = "Off Domain"; Value = "Off-Domain" }
    [void]$tabControl.TabPages.Add($tabPage)
}

# "Off Domain" is a filtered view keyed on the Domain field, not a real Status - a device can
# be both "Current Inventory" and Off-Domain at the same time, so it doesn't get auto-moved
# or manually moved into this view the way the 5 real statuses work. Add/Import are hidden
# since a brand-new row here would need an invalid Status value ("OffDomain" isn't real).
$tabOffDomain = New-InventoryTab -StatusKey "OffDomain" -StatusLabel "Off Domain" -MoveTargets $script:Statuses -RowFilterOverride "[Domain] = 'Off-Domain'" -HideCreateButtons -DomainMoveTarget @{ Label = "On Domain (CONTOSO)"; Value = "CONTOSO" }
[void]$tabControl.TabPages.Add($tabOffDomain)

# "Users with no Devices" isn't a view over $script:Items at all - it's a live AD report (which
# enabled AD users have no inventory record anchored to them), so it gets its own grid bound to
# a plain list instead of a DataView over the shared Items table. Loaded on demand via the
# Refresh button rather than at startup, since it requires a real AD query (and possibly the
# alternate-credential prompt) that shouldn't slow down or block opening the app.
$tabNoDeviceUsers = New-Object System.Windows.Forms.TabPage
$tabNoDeviceUsers.Text = "Users with no Devices"

$topPanelNoDevice = New-Object System.Windows.Forms.Panel
$topPanelNoDevice.Dock = "Top"
$topPanelNoDevice.Height = 40
$tabNoDeviceUsers.Controls.Add($topPanelNoDevice)

$btnRefreshNoDevice = New-Object System.Windows.Forms.Button
$btnRefreshNoDevice.Text = "Refresh from AD"
$btnRefreshNoDevice.Location = New-Object System.Drawing.Point(10, 7)
$btnRefreshNoDevice.Size = New-Object System.Drawing.Size(120, 26)
$topPanelNoDevice.Controls.Add($btnRefreshNoDevice)

$lblNoDeviceStatus = New-Object System.Windows.Forms.Label
$lblNoDeviceStatus.Text = "Click 'Refresh from AD' to load enabled AD users with no device on record."
$lblNoDeviceStatus.Location = New-Object System.Drawing.Point(140, 12)
$lblNoDeviceStatus.Size = New-Object System.Drawing.Size(900, 20)
$lblNoDeviceStatus.ForeColor = [System.Drawing.Color]::DarkRed
$topPanelNoDevice.Controls.Add($lblNoDeviceStatus)

$gridNoDeviceUsers = New-Object System.Windows.Forms.DataGridView
$gridNoDeviceUsers.Dock = "Fill"
$gridNoDeviceUsers.ReadOnly = $true
$gridNoDeviceUsers.AllowUserToAddRows = $false
$gridNoDeviceUsers.SelectionMode = "FullRowSelect"
$gridNoDeviceUsers.MultiSelect = $true
$gridNoDeviceUsers.AutoSizeColumnsMode = "Fill"
$gridNoDeviceUsers.RowHeadersVisible = $false
$gridNoDeviceUsers.AllowUserToResizeRows = $false
$script:GridNoDeviceUsers = $gridNoDeviceUsers

$tabNoDeviceUsers.Controls.Add($gridNoDeviceUsers)
$tabNoDeviceUsers.Controls.Add($topPanelNoDevice)
$gridNoDeviceUsers.BringToFront()

$btnRefreshNoDevice.add_Click({
    $script:GridNoDeviceUsers.Cursor = [System.Windows.Forms.Cursors]::WaitCursor
    $lblNoDeviceStatus.Text = "Querying Active Directory..."
    [System.Windows.Forms.Application]::DoEvents()
    try {
        $results = @(Get-AdUsersWithNoDevice)
        $script:GridNoDeviceUsers.DataSource = [System.Collections.ArrayList]$results
        $lblNoDeviceStatus.Text = "$($results.Count) enabled AD user(s) with no device on record (as of $(Get-Date -Format 'HH:mm:ss'))."
        Write-AppLog "Users with no Devices: found $($results.Count) enabled AD user(s) with no matching inventory record." "Info"
    } finally {
        $script:GridNoDeviceUsers.Cursor = [System.Windows.Forms.Cursors]::Default
    }
})

[void]$tabControl.TabPages.Add($tabNoDeviceUsers)

Load-Inventory
$script:Items.PrimaryKey = @($script:Items.Columns["Id"])
Update-DuplicateGroups
Invoke-PeriodicBackupCheck

$script:BackupTimer = New-Object System.Windows.Forms.Timer
$script:BackupTimer.Interval = $script:BackupIntervalMs
$script:BackupTimer.add_Tick({ Invoke-PeriodicBackupCheck })
$script:BackupTimer.Start()

function Invoke-PeriodicInventoryRefresh {
    # A passive viewer (nobody clicking anything here) would otherwise never see another
    # session's changes until they happened to trigger their own save. This just checks the
    # shared file's last-write time cheaply (no lock needed for that) and only actually reloads
    # when it's genuinely newer than what this session last knew.
    try {
        if (-not (Test-Path $script:DataFile)) { return }
        $currentWriteTimeUtc = (Get-Item $script:DataFile).LastWriteTimeUtc
        if ($null -ne $script:LastKnownFileWriteTimeUtc -and $currentWriteTimeUtc -le $script:LastKnownFileWriteTimeUtc) { return }

        # Every mutating action in this app saves immediately, so this session should never have
        # unsaved local edits sitting around - but check rather than assume, so an automatic
        # background refresh can never silently discard something the operator hasn't saved yet.
        $localSnapshot = Get-InventorySnapshotMap
        $priorSnapshot = if ($null -ne $script:LastSyncSnapshot) { $script:LastSyncSnapshot } else { @{} }
        $hasLocalChanges = $false
        foreach ($id in $localSnapshot.Keys) {
            if (-not $priorSnapshot.ContainsKey($id) -or $priorSnapshot[$id] -ne $localSnapshot[$id]) { $hasLocalChanges = $true; break }
        }
        if (-not $hasLocalChanges) {
            foreach ($id in $priorSnapshot.Keys) {
                if (-not $localSnapshot.ContainsKey($id)) { $hasLocalChanges = $true; break }
            }
        }
        if ($hasLocalChanges) { return }

        Load-Inventory
        $script:Items.PrimaryKey = @($script:Items.Columns["Id"])
        Refresh-AllTabs
        Write-AppLog "Inventory refreshed automatically - picked up changes saved by another session." "Info"
    } catch { }
}

$script:InventoryRefreshTimer = New-Object System.Windows.Forms.Timer
$script:InventoryRefreshTimer.Interval = $script:InventoryRefreshMs
$script:InventoryRefreshTimer.add_Tick({ Invoke-PeriodicInventoryRefresh })
$script:InventoryRefreshTimer.Start()

foreach ($s in $script:Statuses) {
    Apply-Filter -StatusKey $s.Key
    Update-TabStatusLabel -StatusKey $s.Key
    $script:Grids[$s.Key].AutoResizeColumns([System.Windows.Forms.DataGridViewAutoSizeColumnsMode]::AllCells)
}
Apply-Filter -StatusKey "OffDomain"
Update-TabStatusLabel -StatusKey "OffDomain"
$script:Grids["OffDomain"].AutoResizeColumns([System.Windows.Forms.DataGridViewAutoSizeColumnsMode]::AllCells)

# KeyPreview lets the form see key presses before whatever control currently has focus does -
# without it, Ctrl+N while a grid or textbox is focused would never reach this handler at all.
$form.KeyPreview = $true
$form.add_KeyDown({
    param($sender, $e)
    if ($e.Control -and $e.KeyCode -eq [System.Windows.Forms.Keys]::N) {
        $e.SuppressKeyPress = $true
        Show-OnboardingWizard
    }
})

$script:StartupAdPromptShown = $false
$form.Add_Shown({
    $form.Activate()
    if (-not $script:StartupAdPromptShown) {
        $script:StartupAdPromptShown = $true
        # The gate account (Invoke-AccessGate) already ran before this window was ever created -
        # it only controls whether the tool launches at all. This SU login is the real
        # authorization check for actually using the tool: it fires now, immediately after the
        # inventory view is on screen, and is mandatory - cancelling or failing it closes the
        # app rather than leaving an unauthenticated session sitting on real inventory data.
        if (-not (Invoke-SuLogin)) {
            [System.Windows.Forms.MessageBox]::Show("Admin login failed or was cancelled. Inventory Manager will now close.", "Inventory Manager", "OK", "Error") | Out-Null
            # Plain `exit` does not reliably terminate the process from inside a nested event
            # handler running under an active ShowDialog() message loop (confirmed by testing -
            # execution can silently stop without the process actually closing).
            # [System.Environment]::Exit terminates immediately at the runtime level instead.
            [System.Environment]::Exit(1)
        }
    }
})
[void]$form.ShowDialog()
