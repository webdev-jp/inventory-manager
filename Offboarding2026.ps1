<#
.SYNOPSIS
    CONTOSO User Offboarding Tool v2 (GUI) - fuzzy user search + expandable step registry.

.DESCRIPTION
    Windows Forms offboarding tool for helpdesk staff.

    NEW IN V2:
      - Fuzzy user search: type a partial name/username/email, get a grid of
        matches from AD (uses ANR + wildcard matching), and pick the right one.
      - Step registry: every offboarding action is a self-contained entry in
        $Script:Steps. To add a NEW action, copy one block and edit it - the
        GUI (tabs + checkboxes), confirmation dialog, test mode, and logging
        all pick it up automatically. See "HOW TO ADD A STEP" below.
      - Presets dropdown (Full offboard / AD only / Mailbox only / Nothing).
      - Test mode, per-run transcript + CSV summary, DC-pinned AD operations,
        Exchange-schema detection, and target-OU validation carried over
        from v1 (including fixes from the mike1 test run).

    2026-09-30 review cleanup:
      - Steps run with ErrorAction Stop, so a failed EXO/AD call now fails its
        step instead of being logged as success.
      - Graph/SPO steps use the UPN; EXO steps use the mail address.
      - OneDrive URL and SPO admin URL come from Graph (no hand-built URLs).
      - License removal is blocked unless the mailbox is confirmed Shared and
        doesn't need a license (>50GB, archive, litigation hold). Group-based
        licenses are reported, not attempted.
      - OneDrive archive (move mode) only deletes a source item after the copy
        is verified at the destination; test mode no longer grants OneDrive access.
      - Cloud steps reordered: revoke sessions -> archive -> OneDrive access ->
        licenses last. Revoke sessions is on by default.
      - User is re-read from AD at the start of every run; the run is wrapped in
        try/finally and the form is locked while it runs.

.NOTES
    Author: TechHelp
    Requires: Windows PowerShell 5.1, ActiveDirectory (RSAT).
    Optional per step: ExchangeOnlineManagement, Microsoft.Graph.Authentication /
    Microsoft.Graph.Users, Microsoft.Online.SharePoint.PowerShell.
#>

#Requires -Version 5.1

param(
    # Optional - pre-fills and auto-runs the AD search box (e.g. a SamAccountName or full name)
    # so the operator lands straight on a candidate list instead of typing it themselves.
    # Passed by Inventory Manager's "Offboard User" action; harmless to omit for a standalone run.
    [string]$PrefillSearch,
    # Optional - if set, a JSON session record is kept here for the caller (Inventory Manager):
    # written as soon as the tool starts (so "no file" means the script never ran at all - e.g.
    # blocked by the signature check), rewritten after EVERY Run (who, test mode, ticket, per-step
    # results, and an undo journal of the "before" state of everything a real run changed), and
    # stamped with ClosedAt when the window closes. In -UndoFrom mode it receives the undo report.
    [string]$ResultFile,
    # Optional - UNDO MODE. Path to a session record previously written via -ResultFile. Instead of
    # the normal tool, a small window opens and reverses every journaled change made to -UndoUser
    # by real runs in that session, newest first, then writes an undo report to -ResultFile.
    # Launched by Inventory Manager's "Undo offboarding" button; can also be run by hand.
    [string]$UndoFrom,
    # SamAccountName whose changes to undo (required with -UndoFrom).
    [string]$UndoUser,
    # Optional - path to a PSCredential exported via Export-Clixml (DPAPI-encrypted, readable
    # only by the same Windows user/machine that wrote it). If given, it's imported once, used
    # for every on-prem AD cmdlet call in this script (so offboarding runs as this account
    # rather than whoever is logged into Windows), and the file is deleted immediately after
    # loading - it's a one-time handoff, not something that should linger on disk.
    [string]$CredentialFile
)

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

#region ================= CONFIG =================
$Script:LogPath           = "C:\Temp\Offboarding"
# Only used as a fallback / sanity check - the real SPO admin URL is derived from the
# user's OneDrive host (via Graph) at run time, and a mismatch is logged as a warning.
$Script:SPOAdminUrl       = "https://contoso-admin.sharepoint.com"
# IMPORTANT: this OU must stay INSIDE the Entra Connect sync scope (it is, as of 2026-09-30).
# If it's ever removed, the Move + Delta sync steps soft-delete the user in Entra ID, which
# takes the (shared) mailbox and OneDrive into deleted-retention along with it.
$Script:DefaultDisabledOU = "OU=Disabled users,OU=Domain Users,DC=contoso,DC=local"
$Script:ADSyncServer      = "ADSYNC01"
$Script:MaxSearchResults  = 25
$Script:SharedMbxMaxBytes = 50GB   # shared mailboxes over this size still need a license

# --- Credential handling ---
# The offboarding credential is stored in each operator's Windows Credential
# Manager (encrypted per-user by Windows DPAPI). It is NOT stored in this file.
# $Script:CredTarget is the "target name" the credential is saved under.
# On first run, if the credential isn't found, the operator is prompted once
# and it's saved for next time. Set $Script:UseStoredCred = $false to always
# fall back to interactive sign-in prompts instead.
# NOTE: a stored password only works for EXO while that account is NOT subject to
# MFA / Conditional Access. If it's rejected, the tool falls back to the interactive
# sign-in window automatically.
$Script:UseStoredCred = $true
$Script:CredTarget    = "Offboarding"     # name in Credential Manager
$Script:Cred          = $null                  # populated at runtime (EXO sign-in)

# On-prem AD credential (separate from $Script:Cred above, which is for EXO only).
# Splat this into every Get-AD*/Set-AD*/Disable-AD*/Move-AD*/Remove-AD* call (and the remote
# ADSync call) so, when a caller supplies -CredentialFile, offboarding's AD actions run as that
# account instead of whoever is logged into Windows. Empty hashtable (no -Credential passed)
# when none was supplied, same pattern Inventory Manager itself uses for this.
$Script:AdCredParams = @{}
if ($CredentialFile -and (Test-Path $CredentialFile)) {
    try {
        $adCred = Import-Clixml -Path $CredentialFile
        $Script:AdCredParams = @{ Credential = $adCred }
    }
    catch { Write-Warning "Could not load the AD credential file: $($_.Exception.Message)" }
    finally { Remove-Item -Path $CredentialFile -Force -ErrorAction SilentlyContinue }
}
# Who the AD changes are actually made as - used in the Description stamp and summary.
$Script:OperatorName = if ($Script:AdCredParams.Credential) { $Script:AdCredParams.Credential.UserName } else { $env:USERNAME }

# --- OneDrive -> SharePoint archive target ---
# Departed users' OneDrive files are archived here. Set $Script:ArchiveFolderUrl
# to the destination FOLDER's normal URL (what you see in the browser address bar
# when you open the folder in SharePoint). The tool resolves it via Microsoft
# Graph, creates a per-user subfolder inside it named after the user's
# SamAccountName, and server-side-copies the OneDrive there.
# If path resolution ever fails, it falls back to $Script:ArchiveShareLink
# (a "Copy link" sharing URL to the same folder), so both are worth setting.
$Script:ArchiveFolderUrl = "https://contoso.sharepoint.com/Archived_H_Drives/Exec_IL/IT"
$Script:ArchiveShareLink = "https://contoso.sharepoint.com/:f:/g/Archived_H_Drives/IgDLBx3l6GBQTZKz5WqXX-K6AarcoAOEJanq0JIRJXbQWLA"

$Script:GraphScopes = @("User.ReadWrite.All", "Files.ReadWrite.All", "Sites.ReadWrite.All")
$Script:UserProps   = @('MemberOf', 'mail', 'Description', 'DistinguishedName', 'Enabled', 'Title', 'Department', 'UserPrincipalName')
#endregion

#region ================= SHARED STATE / HELPERS =================
$Script:DC = $null
$Script:HasExchangeSchema = $null
$Script:TargetUser = $null
$Script:Transcripting = $false
$Script:Running = $false
$Script:SpoConnectedUrl = $null

function Write-GuiLog {
    param([string]$Message, [string]$Level = "INFO")
    $line = "[{0}] [{1}] {2}" -f (Get-Date -Format 'HH:mm:ss'), $Level, $Message
    $Script:LogBox.AppendText($line + [Environment]::NewLine)
    $Script:LogBox.ScrollToCaret()
    [System.Windows.Forms.Application]::DoEvents()
    if ($Script:Transcripting) { Write-Host $line }
}

function Get-ErrorText {
    # Unwraps the "Exception calling "Invoke" with "2" argument(s)" wrapper that
    # ScriptBlock.Invoke() puts around errors thrown inside step work blocks.
    param($ErrorRecord)
    $ex = $ErrorRecord.Exception
    while ($ex -is [System.Management.Automation.MethodInvocationException] -and $ex.InnerException) { $ex = $ex.InnerException }
    return $ex.Message
}

function Test-ModuleAvailable { param([string]$Name) [bool](Get-Module -ListAvailable -Name $Name) }

function Import-CloudModules {
    # Microsoft.Graph MUST be loaded before ExchangeOnlineManagement in the same session: once EXO
    # 3.x has loaded its own Azure.Core/MSAL assemblies, importing Graph 2.x fails with
    # "Method 'GetTokenAsync' ... does not have an implementation" (found live 2026-09-30).
    # Graph-then-EXO loads cleanly, so every cloud connect goes through here first.
    if (-not (Get-Module Microsoft.Graph.Users) -and (Test-ModuleAvailable 'Microsoft.Graph.Users')) {
        Import-Module Microsoft.Graph.Users -ErrorAction Stop
    }
}

function Get-OffboardCredential {
    # Returns a PSCredential from Windows Credential Manager, prompting once
    # and saving it if not already stored. Returns $null if disabled or the
    # operator cancels the prompt (callers then fall back to interactive auth).
    if (-not $Script:UseStoredCred) { return $null }
    if ($Script:Cred) { return $Script:Cred }

    $haveModule = Test-ModuleAvailable 'CredentialManager'
    if ($haveModule) {
        Import-Module CredentialManager -ErrorAction SilentlyContinue
        $stored = Get-StoredCredential -Target $Script:CredTarget -ErrorAction SilentlyContinue
        if ($stored -and $stored.UserName -notlike '*@*') {
            # e.g. "CONTOSO\someone" - an on-prem logon, which Exchange Online rejects ("User name is
            # malformed"). Found live 2026-09-30. Ignore it rather than failing every run.
            Write-GuiLog "Saved credential '$($Script:CredTarget)' is '$($stored.UserName)' - Exchange Online needs a Microsoft 365 sign-in (name@domain). Ignoring it; use 'Saved Credential...' to replace or clear it." "WARN"
            $stored = $null
        }
        if ($stored) {
            Write-GuiLog "Loaded offboarding credential '$($Script:CredTarget)' from Credential Manager ($($stored.UserName))."
            $Script:Cred = $stored
            return $stored
        }
    }

    # Not stored yet (or module missing) - prompt the operator once.
    $prompt = if ($haveModule) {
        "Enter the Microsoft 365 admin sign-in for Exchange Online (name@contoso.com - NOT DOMAIN\user). It's saved to YOUR Windows Credential Manager (encrypted per-user). Cancel to use the normal sign-in window instead."
    } else {
        "Enter the Microsoft 365 admin sign-in for Exchange Online (name@contoso.com - NOT DOMAIN\user), for this session only. Cancel to use the normal sign-in window instead."
    }
    $cred = Get-Credential -Message $prompt
    if (-not $cred) { Write-GuiLog "Credential prompt cancelled - falling back to interactive sign-in." "WARN"; return $null }
    if ($cred.UserName -notlike '*@*') {
        Write-GuiLog "'$($cred.UserName)' isn't a Microsoft 365 sign-in (name@domain) - not saving it; using the normal sign-in window instead." "WARN"
        return $null
    }

    if ($haveModule) {
        try {
            New-StoredCredential -Target $Script:CredTarget -UserName $cred.UserName `
                -Password $cred.GetNetworkCredential().Password -Persist LocalMachine -Type Generic -ErrorAction Stop | Out-Null
            Write-GuiLog "Credential saved to Credential Manager as '$($Script:CredTarget)'."
        }
        catch { Write-GuiLog "Could not save credential (using for this session only): $($_.Exception.Message)" "WARN" }
    }
    $Script:Cred = $cred
    return $cred
}

function Connect-EXOIfNeeded {
    if (-not (Test-ModuleAvailable 'ExchangeOnlineManagement')) { throw "ExchangeOnlineManagement module not installed. Run: Install-Module ExchangeOnlineManagement" }
    Import-CloudModules
    Import-Module ExchangeOnlineManagement -ErrorAction Stop
    if (Get-ConnectionInformation) { return }
    # -DisableWAM: the Windows sign-in broker never surfaced its window when called from this
    # tool, so the run just hung at "sign-in window may appear" (found live 2026-09-30). Without
    # WAM, EXO uses its own sign-in window / browser prompt, which does appear.
    $cred = Get-OffboardCredential
    if ($cred) {
        try {
            Write-GuiLog "Connecting to Exchange Online as $($cred.UserName)..."
            Connect-ExchangeOnline -Credential $cred -ShowBanner:$false -DisableWAM -ErrorAction Stop
            return
        }
        catch { Write-GuiLog "Stored-credential sign-in rejected (MFA/Conditional Access?): $($_.Exception.Message) - falling back to interactive sign-in." "WARN" }
    }
    Write-GuiLog "Connecting to Exchange Online - a Microsoft sign-in window will open (check the taskbar if you don't see it)..."
    Connect-ExchangeOnline -ShowBanner:$false -DisableWAM -ErrorAction Stop
}

function Connect-GraphIfNeeded {
    if (-not (Test-ModuleAvailable 'Microsoft.Graph.Users')) { throw "Microsoft.Graph.Users module not installed. Run: Install-Module Microsoft.Graph.Users" }
    if ((Get-Module ExchangeOnlineManagement) -and -not (Get-Module Microsoft.Graph.Authentication)) {
        throw "Microsoft Graph can't load after Exchange Online in the same session. Close the tool and run again - it loads Graph first now."
    }
    Import-CloudModules
    # Same reason as -DisableWAM on Connect-ExchangeOnline: the broker window never appears here.
    if (Get-Command Set-MgGraphOption -ErrorAction SilentlyContinue) { Set-MgGraphOption -DisableLoginByWAM $true | Out-Null }
    $mg = Get-MgContext
    $missing = if ($mg) { $Script:GraphScopes | Where-Object { $mg.Scopes -notcontains $_ } } else { $Script:GraphScopes }
    if ($missing) {
        # Graph interactive uses the browser/device flow; stored credentials
        # can't be passed to delegated Connect-MgGraph without an app
        # registration, so this always uses the sign-in prompt. If the operator
        # is already signed in to Windows with the offboarding account, SSO
        # makes this seamless. Also reconnects when an existing session lacks
        # one of the scopes this tool needs.
        Write-GuiLog "Connecting to Microsoft Graph (sign-in window may appear)..."
        Connect-MgGraph -Scopes $Script:GraphScopes -NoWelcome -ErrorAction Stop
    }
}

function Get-UserOneDriveUrl {
    # Asks Graph for the user's real OneDrive site URL (handles renamed UPNs, collision
    # suffixes, and whichever tenant host this actually is) instead of building it by hand.
    param([string]$Upn)
    Connect-GraphIfNeeded
    $drive = Invoke-MgGraphRequest -Method GET -Uri "https://graph.microsoft.com/v1.0/users/$Upn/drive?`$select=webUrl"
    if (-not $drive.webUrl) { throw "Graph returned no OneDrive for $Upn (not provisioned?)." }
    # webUrl is .../personal/<user>/Documents - the site is its parent.
    return ($drive.webUrl.TrimEnd('/') -replace '/[^/]+$', '')
}

function Connect-SPOForSite {
    # Connects to the SPO admin endpoint that matches the given OneDrive/SharePoint site's tenant.
    param([string]$SiteUrl)
    if (-not (Test-ModuleAvailable 'Microsoft.Online.SharePoint.PowerShell')) {
        throw "Microsoft.Online.SharePoint.PowerShell module not installed (needed to grant OneDrive access)."
    }
    Import-Module Microsoft.Online.SharePoint.PowerShell -DisableNameChecking -ErrorAction Stop
    $tenant   = ([Uri]$SiteUrl).Host -replace '(-my)?\.sharepoint\.com$', ''
    $adminUrl = "https://$tenant-admin.sharepoint.com"
    if ($adminUrl -ne $Script:SPOAdminUrl.TrimEnd('/')) {
        Write-GuiLog "Configured SPOAdminUrl ($($Script:SPOAdminUrl)) doesn't match this tenant - using $adminUrl. Update `$Script:SPOAdminUrl." "WARN"
    }
    if ($Script:SpoConnectedUrl -ne $adminUrl) {
        Write-GuiLog "Connecting to SharePoint admin $adminUrl (sign-in window may appear)..."
        Connect-SPOService -Url $adminUrl -ErrorAction Stop
        $Script:SpoConnectedUrl = $adminUrl
    }
}

function Grant-OneDriveAdmin {
    param([string]$SiteUrl, [string]$AdminUpn)
    Connect-SPOForSite -SiteUrl $SiteUrl
    Set-SPOUser -Site $SiteUrl -LoginName $AdminUpn -IsSiteCollectionAdmin $true -ErrorAction Stop | Out-Null
}

function ConvertTo-GraphShareId {
    # Encodes a SharePoint/OneDrive sharing URL into the Graph "shares" id format.
    param([string]$Url)
    $b64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($Url))
    return "u!" + ($b64.TrimEnd('=') -replace '/', '_' -replace '\+', '-')
}

function Resolve-ArchiveTarget {
    # Returns @{ DriveId; FolderId; Name } for the archive destination.
    # Strategy 1: resolve the plain folder URL by site -> drive (matched by
    #   webUrl prefix) -> folder-by-relative-path. Deterministic and precise.
    # Strategy 2 (fallback): resolve the sharing link via the Graph shares API.
    param([scriptblock]$Log)

    # ---- Strategy 1: folder URL ----
    if ($Script:ArchiveFolderUrl) {
        try {
            $u = [Uri]$Script:ArchiveFolderUrl
            $spHost = $u.Host
            # Root site of the host (folder URL has no /sites/ segment here).
            $site = Invoke-MgGraphRequest -Method GET -Uri "https://graph.microsoft.com/v1.0/sites/$spHost"
            $siteId = $site.id
            $drives = (Invoke-MgGraphRequest -Method GET -Uri "https://graph.microsoft.com/v1.0/sites/$siteId/drives").value
            # Pick the drive whose webUrl is a prefix of the target folder URL.
            $target = $Script:ArchiveFolderUrl.TrimEnd('/')
            $drive = $drives | Where-Object { $target -like ($_.webUrl.TrimEnd('/') + '*') } |
                     Sort-Object { $_.webUrl.Length } -Descending | Select-Object -First 1
            if (-not $drive) { throw "No document library on $spHost matched the folder URL." }
            $relPath = $target.Substring($drive.webUrl.TrimEnd('/').Length).Trim('/')
            $relPath = [Uri]::UnescapeDataString($relPath)
            $folder = if ($relPath) {
                Invoke-MgGraphRequest -Method GET -Uri "https://graph.microsoft.com/v1.0/drives/$($drive.id)/root:/$($relPath):"
            } else {
                Invoke-MgGraphRequest -Method GET -Uri "https://graph.microsoft.com/v1.0/drives/$($drive.id)/root"
            }
            & $Log "Archive target resolved by path: library '$($drive.name)', folder '$($folder.name)'."
            return @{ DriveId = $drive.id; FolderId = $folder.id; Name = $folder.name }
        }
        catch { & $Log "Path resolution failed ($($_.Exception.Message)); trying the sharing link..." "WARN" }
    }

    # ---- Strategy 2: sharing link ----
    if ($Script:ArchiveShareLink) {
        $shareId = ConvertTo-GraphShareId $Script:ArchiveShareLink
        $destItem = Invoke-MgGraphRequest -Method GET -Uri "https://graph.microsoft.com/v1.0/shares/$shareId/driveItem"
        if (-not $destItem.parentReference.driveId -or -not $destItem.id) { throw "Sharing link did not resolve to a folder (is it a FOLDER share link?)." }
        & $Log "Archive target resolved by sharing link: folder '$($destItem.name)'."
        return @{ DriveId = $destItem.parentReference.driveId; FolderId = $destItem.id; Name = $destItem.name }
    }

    throw "No archive destination could be resolved. Set `$Script:ArchiveFolderUrl or `$Script:ArchiveShareLink."
}

function Wait-GraphCopy {
    # Polls a copy monitor URL until the async server-side copy finishes.
    # Only 'completed' or a 404 (monitor gone once the job is done) count as done; other
    # polling errors are treated as transient, and 5 in a row give up as "not confirmed".
    # Callers still verify the destination item before trusting the copy.
    param([string]$MonitorUrl, [int]$TimeoutSec = 300, [scriptblock]$Log)
    if (-not $MonitorUrl) { return $true }  # some copies complete synchronously
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    $pollFailures = 0
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 3
        try {
            $status = Invoke-RestMethod -Uri $MonitorUrl -Method GET -ErrorAction Stop
            $pollFailures = 0
        }
        catch {
            $code = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 0 }
            if ($code -eq 404) { return $true }
            if (++$pollFailures -ge 5) { & $Log "  Copy status check kept failing ($($_.Exception.Message))." "WARN"; return $false }
            continue
        }
        switch ($status.status) {
            'completed'  { return $true }
            'failed'     { & $Log "  Copy failed: $($status.errorCode)$($status.error.message)" "WARN"; return $false }
            default      { }  # inProgress / notStarted - keep polling
        }
    }
    & $Log "  Copy timed out after ${TimeoutSec}s (may still complete server-side)." "WARN"
    return $false
}

function Test-ArchivedCopy {
    # Confirms the copied item exists in the archive subfolder and is at least as large as
    # the source. Folder sizes can lag a few seconds behind the copy, so this retries.
    param([string]$DriveId, [string]$ParentId, $SourceItem)
    $name = [Uri]::EscapeDataString($SourceItem.name)
    for ($i = 0; $i -lt 5; $i++) {
        try {
            $d = Invoke-MgGraphRequest -Method GET -Uri "https://graph.microsoft.com/v1.0/drives/$DriveId/items/$($ParentId):/$($name)?`$select=id,size"
            if ([int64]$d.size -ge [int64]$SourceItem.size) { return $true }
        } catch { }
        Start-Sleep -Seconds 3
    }
    return $false
}

function Invoke-OneDriveArchive {
    # Resolves the archive destination via Graph, creates a per-user subfolder
    # inside it, and server-side-copies the user's OneDrive there (recursive,
    # no local staging). With -Move, deletes the source items only after every
    # copy is verified at the destination. Returns a stats hashtable.
    param(
        [Parameter(Mandatory)] [string]$Upn,
        [Parameter(Mandatory)] [string]$UserFolderName,
        [switch]$Move,
        [switch]$DryRun,
        [scriptblock]$Log
    )
    Connect-GraphIfNeeded   # needs Files.ReadWrite.All + Sites.ReadWrite.All
    # The account doing the Graph copy is the one that needs OneDrive access.
    $operator = (Get-MgContext).Account

    # ---- Resolve the destination folder (by path, or sharing-link fallback) ----
    $dest = Resolve-ArchiveTarget -Log $Log
    $destDriveId  = $dest.DriveId
    $destFolderId = $dest.FolderId

    # Make sure the operator can read the source OneDrive (SPO site-admin grant).
    $odUrl = Get-UserOneDriveUrl -Upn $Upn
    # What this run actually did, kept up to date as it goes so the step can journal it for the
    # undo report even if the archive fails partway through.
    $Script:LastArchive = @{ Operator = $operator; OneDrive = $odUrl; Folder = $null; Copied = 0; Deleted = 0 }
    if ($DryRun) {
        & $Log "WOULD: make $operator a site admin on $odUrl (needed to read it)." "TEST"
    }
    else {
        try { Grant-OneDriveAdmin -SiteUrl $odUrl -AdminUpn $operator; & $Log "Confirmed $operator has OneDrive access ($odUrl)." }
        catch { & $Log "Could not confirm OneDrive admin access (may still work if already owner): $($_.Exception.Message)" "WARN" }
    }

    # ---- Enumerate the source OneDrive root (top-level items; copy is recursive) ----
    $items = @()
    try {
        $srcDrive = Invoke-MgGraphRequest -Method GET -Uri "https://graph.microsoft.com/v1.0/users/$Upn/drive"
        $srcDriveId = $srcDrive.id
        $uri = "https://graph.microsoft.com/v1.0/drives/$srcDriveId/root/children?`$top=200"
        do {
            $page = Invoke-MgGraphRequest -Method GET -Uri $uri
            $items += $page.value
            $uri = $page.'@odata.nextLink'
        } while ($uri)
    }
    catch {
        if ($DryRun) {
            & $Log "Can't list the OneDrive yet ($($_.Exception.Message)) - expected in test mode until the site-admin grant above is real." "WARN"
            return @{ Items = 0; Copied = 0; Deleted = 0; Cleared = $false; Simulated = $true }
        }
        throw
    }
    $count = @($items).Count
    & $Log "Found $count top-level item(s) in OneDrive."

    if ($DryRun) {
        & $Log "WOULD: create subfolder '$UserFolderName' in the archive and copy $count top-level item(s) there." "TEST"
        if ($Move) { & $Log "WOULD: then delete the source items from OneDrive (move mode, only once every copy is verified)." "TEST" }
        return @{ Items = $count; Copied = 0; Deleted = 0; Cleared = $false; Simulated = $true }
    }
    if ($count -eq 0) { & $Log "OneDrive is empty - nothing to archive."; return @{ Items = 0; Copied = 0; Deleted = 0; Cleared = $false; Simulated = $false } }

    # ---- Create the per-user subfolder in the destination ----
    $body = @{ name = $UserFolderName; folder = @{}; "@microsoft.graph.conflictBehavior" = "rename" } | ConvertTo-Json
    $subFolder = Invoke-MgGraphRequest -Method POST -Uri "https://graph.microsoft.com/v1.0/drives/$destDriveId/items/$destFolderId/children" -Body $body -ContentType "application/json"
    $subFolderId = $subFolder.id
    $Script:LastArchive.Folder = "$($dest.Name)/$($subFolder.name)"
    & $Log "Created archive subfolder '$($subFolder.name)'."

    # ---- Copy each top-level item (server-side, recursive), then verify it landed ----
    $copied = 0; $copyOk = @{}
    foreach ($it in $items) {
        $copyBody = @{ parentReference = @{ driveId = $destDriveId; id = $subFolderId }; name = $it.name } | ConvertTo-Json
        try {
            Invoke-MgGraphRequest -Method POST -Uri "https://graph.microsoft.com/v1.0/drives/$srcDriveId/items/$($it.id)/copy" `
                -Body $copyBody -ContentType "application/json" -ResponseHeadersVariable rh -StatusCodeVariable sc | Out-Null
            $monitor = if ($rh.Location) { [string]$rh.Location } elseif ($rh['Location']) { [string]$rh['Location'] } else { $null }
            if (-not (Wait-GraphCopy -MonitorUrl $monitor -Log $Log)) { & $Log "  Copy did not confirm complete: $($it.name)" "WARN"; continue }
            if (Test-ArchivedCopy -DriveId $destDriveId -ParentId $subFolderId -SourceItem $it) { $copied++; $copyOk[$it.id] = $true; & $Log "  Copied + verified: $($it.name)" }
            else { & $Log "  Copy reported done but the archive copy is missing or smaller than the source: $($it.name)" "WARN" }
        }
        catch { & $Log "  Copy failed ($($it.name)): $($_.Exception.Message)" "WARN" }
    }
    $Script:LastArchive.Copied = $copied
    & $Log "Copied $copied/$count item(s) to the archive."

    # ---- Move mode: delete only when EVERY copy verified ----
    $deleted = 0; $cleared = $false
    if ($Move) {
        if ($copied -eq $count) {
            foreach ($it in $items) {
                if (-not $copyOk[$it.id]) { continue }
                try { Invoke-MgGraphRequest -Method DELETE -Uri "https://graph.microsoft.com/v1.0/drives/$srcDriveId/items/$($it.id)" | Out-Null; $deleted++ }
                catch { & $Log "  Could not delete source ($($it.name)): $($_.Exception.Message)" "WARN" }
            }
            $Script:LastArchive.Deleted = $deleted
            $cleared = ($deleted -eq $count)
            & $Log "Move mode: deleted $deleted source item(s) from OneDrive (recycle bin)."
        }
        else {
            & $Log "Move mode: NOT deleting source - only $copied of $count copies verified. Files left in place for safety." "WARN"
        }
    }

    if ($copied -lt $count) { throw "Only $copied of $count OneDrive item(s) were archived - see warnings above." }
    return @{ Items = $count; Copied = $copied; Deleted = $deleted; Cleared = $cleared; Simulated = $false }
}

function Get-MailboxBytes {
    # EXO returns TotalItemSize as text like "1.2 GB (1,288,490,188 bytes)".
    param($Stats)
    if (-not $Stats) { return $null }
    $m = [regex]::Match([string]$Stats.TotalItemSize, '\(([\d,]+) bytes\)')
    if ($m.Success) { return [int64]($m.Groups[1].Value -replace ',', '') }
    return $null
}

function Initialize-AD {
    if (-not (Test-ModuleAvailable 'ActiveDirectory')) { throw "ActiveDirectory module not found. Install RSAT." }
    Import-Module ActiveDirectory -ErrorAction Stop
    if (-not $Script:DC) {
        $Script:DC = (Get-ADDomain @Script:AdCredParams).PDCEmulator
        Write-GuiLog "Using DC: $($Script:DC)"
    }
    if ($null -eq $Script:HasExchangeSchema) {
        $rootDSE = Get-ADRootDSE -Server $Script:DC @Script:AdCredParams
        $Script:HasExchangeSchema = [bool](Get-ADObject -Server $Script:DC -SearchBase $rootDSE.SchemaNamingContext -Filter "ldapDisplayName -eq 'msExchHideFromAddressLists'" @Script:AdCredParams -ErrorAction SilentlyContinue)
        if (-not $Script:HasExchangeSchema) { Write-GuiLog "AD schema has no Exchange attributes - GAL hiding will be EXO-only." "WARN" }
    }
}

function Invoke-DeltaSync {
    if (Test-ModuleAvailable 'ADSync') { Import-Module ADSync; Start-ADSyncSyncCycle -PolicyType Delta | Out-Null; return }
    $sync = { Import-Module ADSync; Start-ADSyncSyncCycle -PolicyType Delta }
    # The SU account handed over by Inventory Manager isn't necessarily allowed to remote into the
    # sync server ("Access is denied", found live 2026-09-30) - fall back to the Windows session,
    # which is what this step always used before the AD credential handoff existed.
    if ($Script:AdCredParams.Credential) {
        try { Invoke-Command -ComputerName $Script:ADSyncServer -ScriptBlock $sync @Script:AdCredParams -ErrorAction Stop | Out-Null; return }
        catch {
            $firstError = $_.Exception.Message
            Write-GuiLog "Delta sync as $($Script:OperatorName) was refused ($firstError) - retrying as $env:USERDOMAIN\$env:USERNAME..." "WARN"
        }
    }
    try { Invoke-Command -ComputerName $Script:ADSyncServer -ScriptBlock $sync -ErrorAction Stop | Out-Null }
    catch {
        $msg = "Couldn't start a delta sync on $($Script:ADSyncServer) as $env:USERDOMAIN\$env:USERNAME either: $($_.Exception.Message)"
        throw "$msg The change still reaches the cloud on the next scheduled sync (normally within 30 minutes)."
    }
}

# ---- Session record for the caller (-ResultFile) ----
$Script:Session = [ordered]@{
    SchemaVersion = 1
    Mode          = if ($UndoFrom) { 'Undo' } else { 'Offboard' }
    ExpectedUser  = $PrefillSearch
    StartedAt     = (Get-Date).ToString('o')
    ClosedAt      = $null
    Runs          = New-Object System.Collections.ArrayList
}

function Save-SessionFile {
    # Rewrites the whole record each time (it's small). Never lets a write failure break the tool.
    if (-not $ResultFile) { return }
    try { ConvertTo-Json -InputObject $Script:Session -Depth 12 | Set-Content -Path $ResultFile -Encoding UTF8 -ErrorAction Stop }
    catch { if ($Script:LogBox) { Write-GuiLog "Could not write result file '$ResultFile': $($_.Exception.Message)" "WARN" } }
}
#endregion

#region ================= STEP REGISTRY =================
<#  HOW TO ADD A STEP
    Copy any block below into the $Script:Steps array and edit:
      Id       - unique short name (used in logs/summary)
      Tab      - which GUI tab it appears on ("Active Directory", "Mailbox",
                 "Cloud", or any NEW name - a new tab is created automatically)
      Text     - checkbox label
      Default  - $true = pre-checked
      NeedsMgr - $true = requires the Manager field to be filled in
      Action   - scriptblock that does the work. It receives $ctx with:
                   $ctx.User    (AD user object, freshly re-read at run start)
                   $ctx.Upn     (UserPrincipalName - use for Graph / SharePoint)
                   $ctx.Mail    (mail, else UPN   - use for Exchange Online)
                   $ctx.Mgr     (manager field)   $ctx.Ticket
                   $ctx.DryRun  (test mode bool)  $ctx.Timestamp
                   $ctx.Results (results of steps that already ran, by Id:
                                 "OK" / "Simulated" / "FAILED")
                   $ctx.Log     "message"[, "LEVEL"]      - write to log
                   $ctx.Do      "did X" { real work }     - runs the block,
                                or logs "WOULD: did X" in test mode.
                   $ctx.Record  @{ ... }  - UNDO JOURNAL. Call it INSIDE the Do block,
                                right after the change succeeds, with whatever
                                "before" state undo needs; then add a matching
                                case to Invoke-UndoEntry. For a change that can't
                                be reversed, record @{ NotUndoable = "what to do" }.
                 Steps run in the order listed here, with ErrorAction Stop in
                 effect - but EXO cmdlets live in a module that ignores the
                 caller's preference, so pass -ErrorAction Stop to them explicitly.
                 Throw on fatal problems; the runner catches and records it.
#>
$Script:Steps = @(
    # ---------------- ACTIVE DIRECTORY ----------------
    @{ Id = 'DisableAD'; Tab = 'Active Directory'; Text = 'Disable AD account'; Default = $true; NeedsMgr = $false
       Action = {
           param($ctx)
           if (-not $ctx.User.Enabled) { $ctx.Log.Invoke("AD account is already disabled."); return }
           $ctx.Do.Invoke("AD account disabled.", {
               Disable-ADAccount -Identity $ctx.User -Server $Script:DC @Script:AdCredParams
               $ctx.Record.Invoke(@{ WasEnabled = $true })
           })
       } },

    @{ Id = 'RandomPassword'; Tab = 'Active Directory'; Text = 'Randomize password'; Default = $true; NeedsMgr = $false
       Action = {
           param($ctx)
           $ctx.Do.Invoke("Password randomized.", {
               $bytes = New-Object byte[] 24
               [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
               $pw = ConvertTo-SecureString ([Convert]::ToBase64String($bytes) + "!Aa1") -AsPlainText -Force
               Set-ADAccountPassword -Identity $ctx.User -NewPassword $pw -Reset -Server $Script:DC @Script:AdCredParams
               $ctx.Record.Invoke(@{ NotUndoable = "Password was randomized - the old one can't be restored. Reset it and give the user the new one." })
           })
       } },

    @{ Id = 'StampDescription'; Tab = 'Active Directory'; Text = 'Stamp description with offboard date + ticket (keeps the old one)'; Default = $true; NeedsMgr = $false
       Action = {
           param($ctx)
           $note = "Offboarded $(Get-Date -Format 'yyyy-MM-dd') by $($Script:OperatorName) - Ticket: $($ctx.Ticket)"
           $old = [string]$ctx.User.Description
           # Keep whatever was there (Inventory Manager reads the Description) unless this is a re-run.
           if ($old -and $old -notlike 'Offboarded *') { $note = "$note | Was: $old" }
           if ($note.Length -gt 1024) { $note = $note.Substring(0, 1024) }
           $ctx.Do.Invoke("Description stamped: $note", {
               Set-ADUser -Identity $ctx.User -Server $Script:DC -Description $note @Script:AdCredParams
               $ctx.Record.Invoke(@{ OldDescription = $old })
           })
       } },

    @{ Id = 'HideGAL_AD'; Tab = 'Active Directory'; Text = 'Hide from GAL (AD attribute, if schema supports it)'; Default = $true; NeedsMgr = $false
       Action = {
           param($ctx)
           if (-not $Script:HasExchangeSchema) {
               $ctx.Log.Invoke("Skipped AD GAL attribute (no Exchange schema) - EXO hiding in 'Convert to Shared' covers cloud-only users; synced users can't be hidden without it.", "WARN")
               return
           }
           $oldHide = (Get-ADUser -Identity $ctx.User -Server $Script:DC -Properties msExchHideFromAddressLists @Script:AdCredParams).msExchHideFromAddressLists
           if ($oldHide -eq $true) { $ctx.Log.Invoke("Already hidden from GAL (AD attribute)."); return }
           $ctx.Do.Invoke("Hidden from GAL (AD attribute).", {
               Set-ADUser -Identity $ctx.User -Server $Script:DC -Replace @{ msExchHideFromAddressLists = $true } @Script:AdCredParams
               $ctx.Record.Invoke(@{ OldValue = $oldHide })
           })
       } },

    @{ Id = 'ExportGroups'; Tab = 'Active Directory'; Text = 'Export group memberships to CSV (before removal)'; Default = $true; NeedsMgr = $false
       Action = {
           param($ctx)
           $path = Join-Path $Script:LogPath "Groups_$($ctx.User.SamAccountName)_$($ctx.Timestamp).csv"
           # Read-only: runs even in test mode so the export can be verified
           if (-not $ctx.User.MemberOf) { $ctx.Log.Invoke("User has no group memberships (besides the primary group) - nothing to export."); return }
           $ctx.User.MemberOf | ForEach-Object { [PSCustomObject]@{ User = $ctx.User.SamAccountName; GroupDN = $_ } } |
               Export-Csv -Path $path -NoTypeInformation
           $ctx.Log.Invoke("Group memberships exported to $path")
       } },

    @{ Id = 'RemoveGroups'; Tab = 'Active Directory'; Text = 'Remove all group memberships'; Default = $true; NeedsMgr = $false
       Action = {
           param($ctx)
           if ($ctx.DryRun) {
               $ctx.Log.Invoke("WOULD: Remove from $($ctx.User.MemberOf.Count) group(s):", "TEST")
               $ctx.User.MemberOf | ForEach-Object { $ctx.Log.Invoke("WOULD:   - $_", "TEST") }
               return
           }
           $removed = New-Object System.Collections.ArrayList; $failed = 0
           foreach ($g in $ctx.User.MemberOf) {
               try { Remove-ADGroupMember -Identity $g -Members $ctx.User -Confirm:$false -Server $Script:DC @Script:AdCredParams -ErrorAction Stop; [void]$removed.Add([string]$g) }
               catch { $failed++; $ctx.Log.Invoke("Group removal failed ($g): $($_.Exception.Message)", "ERROR") }
           }
           if ($removed.Count) { $ctx.Record.Invoke(@{ Groups = $removed }) }
           $ctx.Log.Invoke("Removed from $($removed.Count) group(s).")
           if ($failed) { throw "$failed group removal(s) failed - see log." }
       } },

    @{ Id = 'MoveOU'; Tab = 'Active Directory'; Text = 'Move to Disabled Users OU (path set below)'; Default = $true; NeedsMgr = $false
       Action = {
           param($ctx)
           $targetOU = $Script:TxtOU.Text.Trim()
           try { $null = Get-ADOrganizationalUnit -Identity $targetOU -Server $Script:DC @Script:AdCredParams -ErrorAction Stop }
           catch { throw "Target OU does not exist: $targetOU - fix the path on the Active Directory tab." }
           $originalOU = ($ctx.User.DistinguishedName -split '(?<!\\),', 2)[1]
           if ($originalOU -eq $targetOU) { $ctx.Log.Invoke("Already in $targetOU."); return }
           $ctx.Do.Invoke("Moved to $targetOU.", {
               Move-ADObject -Identity $ctx.User.DistinguishedName -TargetPath $targetOU -Server $Script:DC @Script:AdCredParams
               $ctx.Record.Invoke(@{ OriginalOU = $originalOU })
           })
       } },

    @{ Id = 'DeltaSync'; Tab = 'Active Directory'; Text = "Trigger ADSync delta sync ($($Script:ADSyncServer))"; Default = $true; NeedsMgr = $false
       Action = {
           param($ctx)
           # Not journaled: undo always triggers its own delta sync after reversing any AD change.
           $ctx.Do.Invoke("Delta sync triggered.", { Invoke-DeltaSync })
       } },

    # ---------------- MAILBOX (EXO) ----------------
    @{ Id = 'ExportMbxPerms'; Tab = 'Mailbox'; Text = 'Export mailbox permissions to CSV (before changes)'; Default = $true; NeedsMgr = $false
       Action = {
           param($ctx)
           Connect-EXOIfNeeded
           $path = Join-Path $Script:LogPath "Permissions_$($ctx.User.SamAccountName)_$($ctx.Timestamp).csv"
           $mp = Get-MailboxPermission -Identity $ctx.Mail -ErrorAction Stop | Where-Object { $_.User -notlike "NT AUTHORITY\SELF" -and -not $_.IsInherited }
           $rp = Get-RecipientPermission -Identity $ctx.Mail -ErrorAction Stop | Where-Object { $_.Trustee -notlike "NT AUTHORITY\SELF" }
           @($mp) + @($rp) | Export-Csv -Path $path -NoTypeInformation
           $ctx.Log.Invoke("Mailbox permissions exported to $path")
       } },

    @{ Id = 'ConvertShared'; Tab = 'Mailbox'; Text = 'Convert mailbox to Shared + hide from address lists'; Default = $true; NeedsMgr = $false
       Action = {
           param($ctx)
           Connect-EXOIfNeeded
           $mbx = Get-Mailbox -Identity $ctx.Mail -ErrorAction Stop
           if ($mbx.RecipientTypeDetails -eq 'SharedMailbox') { $ctx.Log.Invoke("Mailbox is already Shared.") }
           else {
               $ctx.Do.Invoke("Mailbox converted to Shared.", {
                   Set-Mailbox -Identity $ctx.Mail -Type Shared -ErrorAction Stop
                   $ctx.Record.Invoke(@{ OldType = [string]$mbx.RecipientTypeDetails })
               })
           }

           if ($mbx.IsDirSynced) {
               # HiddenFromAddressListsEnabled is mastered on-prem for synced users; EXO rejects it.
               $how = if ($Script:HasExchangeSchema) { "the 'Hide from GAL (AD attribute)' step handles it" } else { "no Exchange schema on-prem, so it can't be hidden - do it manually" }
               $ctx.Log.Invoke("Skipped EXO address-list hiding: user is synced from AD ($how).", "WARN")
           }
           elseif ($mbx.HiddenFromAddressListsEnabled) { $ctx.Log.Invoke("Already hidden from address lists.") }
           else {
               $ctx.Do.Invoke("Hidden from address lists.", {
                   Set-Mailbox -Identity $ctx.Mail -HiddenFromAddressListsEnabled $true -ErrorAction Stop
                   $ctx.Record.Invoke(@{ OldHidden = $false })
               })
           }

           $bytes = Get-MailboxBytes (Get-MailboxStatistics -Identity $ctx.Mail -ErrorAction SilentlyContinue)
           if ($null -ne $bytes) {
               $gb = [Math]::Round($bytes / 1GB, 2)
               $lvl = if ($bytes -gt $Script:SharedMbxMaxBytes) { "WARN" } else { "INFO" }
               $ctx.Log.Invoke("Mailbox size: $gb GB (over 50 GB requires a retained license).", $lvl)
           }
       } },

    @{ Id = 'ForwardMail'; Tab = 'Mailbox'; Text = 'Forward mail to manager (deliver + forward)'; Default = $false; NeedsMgr = $true
       Action = {
           param($ctx)
           Connect-EXOIfNeeded
           $before = Get-Mailbox -Identity $ctx.Mail -ErrorAction Stop
           $ctx.Do.Invoke("Mail forwarding to $($ctx.Mgr) enabled (deliver + forward).", {
               Set-Mailbox -Identity $ctx.Mail -ForwardingAddress $ctx.Mgr -DeliverToMailboxAndForward $true -ErrorAction Stop
               $ctx.Record.Invoke(@{ OldForwardingAddress = [string]$before.ForwardingAddress; OldDeliverAndForward = [bool]$before.DeliverToMailboxAndForward })
           })
       } },

    @{ Id = 'AutoReply'; Tab = 'Mailbox'; Text = 'Enable auto-reply (references manager if set)'; Default = $false; NeedsMgr = $false
       Action = {
           param($ctx)
           Connect-EXOIfNeeded
           $msg = if ($ctx.Mgr) { "$($ctx.User.Name) is no longer with Contoso. Please contact $($ctx.Mgr) for assistance." }
                  else { "$($ctx.User.Name) is no longer with Contoso." }
           $before = Get-MailboxAutoReplyConfiguration -Identity $ctx.Mail -ErrorAction Stop
           $ctx.Do.Invoke("Auto-reply enabled: `"$msg`"", {
               Set-MailboxAutoReplyConfiguration -Identity $ctx.Mail -AutoReplyState Enabled -InternalMessage $msg -ExternalMessage $msg -ExternalAudience All -ErrorAction Stop
               $ctx.Record.Invoke(@{ OldState = [string]$before.AutoReplyState; OldInternal = [string]$before.InternalMessage
                                     OldExternal = [string]$before.ExternalMessage; OldAudience = [string]$before.ExternalAudience })
           })
       } },

    @{ Id = 'GrantMbxAccess'; Tab = 'Mailbox'; Text = 'Grant manager Full Access + SendAs'; Default = $false; NeedsMgr = $true
       Action = {
           param($ctx)
           Connect-EXOIfNeeded
           # Only permissions the manager didn't already have get journaled, so undo never strips
           # access they had before this offboarding.
           $hadFull   = [bool](Get-MailboxPermission -Identity $ctx.Mail -User $ctx.Mgr -ErrorAction SilentlyContinue | Where-Object { $_.AccessRights -match 'FullAccess' -and -not $_.Deny })
           $hadSendAs = [bool](Get-RecipientPermission -Identity $ctx.Mail -Trustee $ctx.Mgr -ErrorAction SilentlyContinue | Where-Object { $_.AccessRights -match 'SendAs' })
           $ctx.Do.Invoke("$($ctx.Mgr) granted Full Access + SendAs.", {
               if (-not $hadFull) {
                   Add-MailboxPermission -Identity $ctx.Mail -User $ctx.Mgr -AccessRights FullAccess -InheritanceType All -AutoMapping $true -ErrorAction Stop | Out-Null
                   $ctx.Record.Invoke(@{ Manager = $ctx.Mgr; Right = 'FullAccess' })
               }
               if (-not $hadSendAs) {
                   Add-RecipientPermission -Identity $ctx.Mail -Trustee $ctx.Mgr -AccessRights SendAs -Confirm:$false -ErrorAction Stop | Out-Null
                   $ctx.Record.Invoke(@{ Manager = $ctx.Mgr; Right = 'SendAs' })
               }
           })
       } },

    @{ Id = 'DisableProtocols'; Tab = 'Mailbox'; Text = 'Disable ActiveSync / OWA / IMAP / POP on the mailbox'; Default = $false; NeedsMgr = $false
       Action = {
           param($ctx)
           Connect-EXOIfNeeded
           $before = Get-CASMailbox -Identity $ctx.Mail -ErrorAction Stop
           $ctx.Do.Invoke("ActiveSync/OWA/IMAP/POP disabled.", {
               Set-CASMailbox -Identity $ctx.Mail -ActiveSyncEnabled $false -OWAEnabled $false -ImapEnabled $false -PopEnabled $false -ErrorAction Stop
               $ctx.Record.Invoke(@{ ActiveSync = [bool]$before.ActiveSyncEnabled; OWA = [bool]$before.OWAEnabled
                                     Imap = [bool]$before.ImapEnabled; Pop = [bool]$before.PopEnabled })
           })
       } },

    # ---------------- CLOUD ----------------
    # Order matters: revoke first (security), archive while the OneDrive is still licensed,
    # and remove licenses LAST, only once the mailbox is confirmed not to need one.
    @{ Id = 'RevokeSessions'; Tab = 'Cloud'; Text = 'Revoke all Entra sign-in sessions / refresh tokens'; Default = $true; NeedsMgr = $false
       Action = {
           param($ctx)
           Connect-GraphIfNeeded
           $ctx.Do.Invoke("All sign-in sessions revoked.", {
               Invoke-MgGraphRequest -Method POST -Uri "https://graph.microsoft.com/v1.0/users/$($ctx.Upn)/revokeSignInSessions" | Out-Null
               $ctx.Record.Invoke(@{ NotUndoable = "Sign-in sessions were revoked - harmless, the user just signs in again." })
           })
       } },

    @{ Id = 'ArchiveOneDrive'; Tab = 'Cloud'; Text = "Archive OneDrive files to SharePoint (move: copy, verify, then clear)"; Default = $false; NeedsMgr = $false
       Action = {
           param($ctx)
           $Script:LastArchive = $null
           try {
               $stats = Invoke-OneDriveArchive -Upn $ctx.Upn -UserFolderName $ctx.User.SamAccountName `
                            -Move -DryRun:$ctx.DryRun -Log $ctx.Log
               $ctx.Log.Invoke("Archive result: found=$($stats.Items) copied=$($stats.Copied) deleted=$($stats.Deleted) cleared=$($stats.Cleared)")
           }
           finally {
               # Journal what really happened (even on a partial failure) - undo can't move files
               # back, so this goes in the undo report as a manual follow-up.
               $a = $Script:LastArchive
               if (-not $ctx.DryRun -and $a -and ($a.Folder -or $a.Deleted)) {
                   $ctx.Record.Invoke(@{ NotUndoable = "OneDrive files were archived to '$($a.Folder)' ($($a.Copied) item(s) copied, $($a.Deleted) removed from OneDrive into its recycle bin). Restore them from the recycle bin or copy them back from the archive if needed. $($a.Operator) also kept site-admin access to $($a.OneDrive) - remove it if it's no longer needed." })
               }
           }
       } },

    @{ Id = 'OneDriveAccess'; Tab = 'Cloud'; Text = "Grant manager access to user's OneDrive (SPO)"; Default = $false; NeedsMgr = $true
       Action = {
           param($ctx)
           $odUrl = Get-UserOneDriveUrl -Upn $ctx.Upn
           $wasAdmin = $false
           try { Connect-SPOForSite -SiteUrl $odUrl; $wasAdmin = [bool](Get-SPOUser -Site $odUrl -LoginName $ctx.Mgr -ErrorAction Stop).IsSiteAdmin } catch { }
           if ($wasAdmin) { $ctx.Log.Invoke("$($ctx.Mgr) is already a site admin on $odUrl."); return }
           $ctx.Do.Invoke("$($ctx.Mgr) added as OneDrive site admin: $odUrl", {
               Grant-OneDriveAdmin -SiteUrl $odUrl -AdminUpn $ctx.Mgr
               $ctx.Record.Invoke(@{ Manager = $ctx.Mgr; Site = $odUrl })
           })
       } },

    @{ Id = 'RemoveLicenses'; Tab = 'Cloud'; Text = 'Remove directly-assigned M365 licenses (only if mailbox is safely Shared)'; Default = $false; NeedsMgr = $false
       Action = {
           param($ctx)
           # ---- Safety gate: an unconverted or license-dependent mailbox gets deleted after 30 days ----
           Connect-EXOIfNeeded
           $mbx = Get-Mailbox -Identity $ctx.Mail -ErrorAction SilentlyContinue
           if ($mbx) {
               $blockers = @()
               if ($mbx.RecipientTypeDetails -ne 'SharedMailbox') {
                   if ($ctx.DryRun -and $ctx.Results['ConvertShared'] -eq 'Simulated') {
                       $ctx.Log.Invoke("Mailbox is still $($mbx.RecipientTypeDetails) (conversion was only simulated) - a real run would re-check this.", "TEST")
                   }
                   else { $blockers += "mailbox is $($mbx.RecipientTypeDetails), not Shared (run 'Convert to Shared' first)" }
               }
               if ($mbx.LitigationHoldEnabled)    { $blockers += "litigation hold is on" }
               if ($mbx.ArchiveStatus -eq 'Active') { $blockers += "an archive mailbox is enabled" }
               $bytes = Get-MailboxBytes (Get-MailboxStatistics -Identity $ctx.Mail -ErrorAction SilentlyContinue)
               if ($bytes -gt $Script:SharedMbxMaxBytes) { $blockers += "mailbox is $([Math]::Round($bytes / 1GB, 1)) GB (over 50 GB)" }
               if ($blockers) { throw "License removal blocked - the mailbox still needs a license: $($blockers -join '; ')." }
           }
           else { $ctx.Log.Invoke("No Exchange Online mailbox found for $($ctx.Mail) - nothing to protect.") }

           # ---- Only direct assignments can be removed; group-based ones come from group membership ----
           Connect-GraphIfNeeded
           $lic = @(Get-MgUserLicenseDetail -UserId $ctx.Upn -ErrorAction Stop)
           if (-not $lic) { $ctx.Log.Invoke("No licenses assigned."); return }
           $states = (Invoke-MgGraphRequest -Method GET -Uri "https://graph.microsoft.com/v1.0/users/$($ctx.Upn)?`$select=licenseAssignmentStates").licenseAssignmentStates
           $directIds = @($states | Where-Object { -not $_.assignedByGroup } | ForEach-Object { $_.skuId } | Select-Object -Unique)
           $groupOnly = $lic | Where-Object { $directIds -notcontains $_.SkuId }
           if ($groupOnly) {
               $ctx.Log.Invoke("Group-assigned license(s) left alone: $($groupOnly.SkuPartNumber -join ', ') - removing the user from the licensing group(s) handles these.", "WARN")
           }
           $direct = $lic | Where-Object { $directIds -contains $_.SkuId }
           if (-not $direct) { $ctx.Log.Invoke("No directly-assigned licenses to remove."); return }
           $ctx.Do.Invoke("Removed license(s): $($direct.SkuPartNumber -join ', ').", {
               Set-MgUserLicense -UserId $ctx.Upn -RemoveLicenses @($direct.SkuId) -AddLicenses @() -ErrorAction Stop | Out-Null
               $ctx.Record.Invoke(@{ SkuIds = @($direct.SkuId); SkuNames = @($direct.SkuPartNumber) })
           })
       } }
)

# Presets: map preset name -> list of step Ids to check (everything else unchecked)
$Script:Presets = [ordered]@{
    'Full offboard'   = $Script:Steps.Id
    'Standard (defaults)' = ($Script:Steps | Where-Object { $_.Default }).Id
    'AD only'         = ($Script:Steps | Where-Object { $_.Tab -eq 'Active Directory' }).Id
    'Mailbox only'    = ($Script:Steps | Where-Object { $_.Tab -eq 'Mailbox' }).Id
    'Uncheck everything' = @()
}
#endregion

#region ================= UNDO MODE (-UndoFrom) =================
function ConvertTo-MailboxTypeParam {
    # RecipientTypeDetails (what Get-Mailbox reports) -> the value Set-Mailbox -Type accepts.
    param([string]$RecipientTypeDetails)
    switch ($RecipientTypeDetails) {
        'RoomMailbox'      { 'Room' }
        'EquipmentMailbox' { 'Equipment' }
        default            { 'Regular' }
    }
}

function Invoke-UndoEntry {
    # Reverses ONE journal entry and returns a line describing what was restored; throws on failure.
    # Every step that calls $ctx.Record needs a matching case here.
    param($Entry, $Run, $AdUser)
    $d    = $Entry.Data
    $mail = if ($Run.Mail) { $Run.Mail } else { $Run.UserPrincipalName }
    $upn  = $Run.UserPrincipalName
    switch ($Entry.Step) {
        'DisableAD' {
            Enable-ADAccount -Identity $AdUser -Server $Script:DC @Script:AdCredParams
            return "AD account re-enabled."
        }
        'StampDescription' {
            if ($d.OldDescription) {
                Set-ADUser -Identity $AdUser -Server $Script:DC -Description $d.OldDescription @Script:AdCredParams
                return "AD Description restored to: $($d.OldDescription)"
            }
            Set-ADUser -Identity $AdUser -Server $Script:DC -Clear description @Script:AdCredParams
            return "AD Description cleared (it was empty before)."
        }
        'HideGAL_AD' {
            if ($null -eq $d.OldValue) { Set-ADUser -Identity $AdUser -Server $Script:DC -Clear msExchHideFromAddressLists @Script:AdCredParams }
            else { Set-ADUser -Identity $AdUser -Server $Script:DC -Replace @{ msExchHideFromAddressLists = [bool]$d.OldValue } @Script:AdCredParams }
            return "Address book visibility (AD attribute) restored."
        }
        'RemoveGroups' {
            $groups = @($d.Groups); $failed = @()
            foreach ($g in $groups) {
                try { Add-ADGroupMember -Identity $g -Members $AdUser -Server $Script:DC @Script:AdCredParams -ErrorAction Stop }
                catch { $failed += "$g ($($_.Exception.Message))" }
            }
            if ($failed) { throw "Re-added $($groups.Count - $failed.Count) of $($groups.Count) group(s). Failed: $($failed -join '; ')" }
            return "Re-added to $($groups.Count) group(s)."
        }
        'MoveOU' {
            Move-ADObject -Identity $AdUser -TargetPath $d.OriginalOU -Server $Script:DC @Script:AdCredParams
            return "Moved back to $($d.OriginalOU)."
        }
        'ConvertShared' {
            Connect-EXOIfNeeded
            if ($d.OldType) {
                Set-Mailbox -Identity $mail -Type (ConvertTo-MailboxTypeParam $d.OldType) -ErrorAction Stop
                return "Mailbox converted back to $($d.OldType)."
            }
            Set-Mailbox -Identity $mail -HiddenFromAddressListsEnabled $false -ErrorAction Stop
            return "Mailbox shown in address lists again."
        }
        'ForwardMail' {
            Connect-EXOIfNeeded
            if ($d.OldForwardingAddress) {
                Set-Mailbox -Identity $mail -ForwardingAddress $d.OldForwardingAddress -DeliverToMailboxAndForward ([bool]$d.OldDeliverAndForward) -ErrorAction Stop
                return "Mail forwarding restored to $($d.OldForwardingAddress)."
            }
            Set-Mailbox -Identity $mail -ForwardingAddress $null -DeliverToMailboxAndForward $false -ErrorAction Stop
            return "Mail forwarding removed."
        }
        'AutoReply' {
            Connect-EXOIfNeeded
            Set-MailboxAutoReplyConfiguration -Identity $mail -AutoReplyState $d.OldState -InternalMessage $d.OldInternal `
                -ExternalMessage $d.OldExternal -ExternalAudience $d.OldAudience -ErrorAction Stop
            return "Auto-reply restored (state: $($d.OldState))."
        }
        'GrantMbxAccess' {
            Connect-EXOIfNeeded
            if ($d.Right -eq 'FullAccess') {
                Remove-MailboxPermission -Identity $mail -User $d.Manager -AccessRights FullAccess -InheritanceType All -Confirm:$false -ErrorAction Stop
            }
            else {
                Remove-RecipientPermission -Identity $mail -Trustee $d.Manager -AccessRights SendAs -Confirm:$false -ErrorAction Stop
            }
            return "Removed $($d.Right) for $($d.Manager)."
        }
        'DisableProtocols' {
            Connect-EXOIfNeeded
            Set-CASMailbox -Identity $mail -ActiveSyncEnabled ([bool]$d.ActiveSync) -OWAEnabled ([bool]$d.OWA) `
                -ImapEnabled ([bool]$d.Imap) -PopEnabled ([bool]$d.Pop) -ErrorAction Stop
            return "ActiveSync/OWA/IMAP/POP settings restored."
        }
        'OneDriveAccess' {
            Connect-SPOForSite -SiteUrl $d.Site
            Set-SPOUser -Site $d.Site -LoginName $d.Manager -IsSiteCollectionAdmin $false -ErrorAction Stop | Out-Null
            return "Removed $($d.Manager)'s site-admin access to the OneDrive."
        }
        'RemoveLicenses' {
            Connect-GraphIfNeeded
            $add = @(@($d.SkuIds) | ForEach-Object { @{ SkuId = $_ } })
            Set-MgUserLicense -UserId $upn -AddLicenses $add -RemoveLicenses @() -ErrorAction Stop | Out-Null
            return "License(s) re-assigned: $(@($d.SkuNames) -join ', ')."
        }
        default { throw "No undo action is defined for step '$($Entry.Step)'." }
    }
}

function Invoke-OffboardUndo {
    # Reverses every journaled change made to $User by REAL runs in the session record, newest
    # change first (so a second run's changes unwind back to the first run's state before the
    # first run's own changes are reversed). Writes the undo report to -ResultFile as it goes.
    param([string]$From, [string]$User)
    $Script:Session = [ordered]@{
        SchemaVersion  = 1
        Mode           = 'UndoReport'
        SamAccountName = $User
        StartedAt      = (Get-Date).ToString('o')
        CompletedAt    = $null
        Undone         = New-Object System.Collections.ArrayList
        Failed         = New-Object System.Collections.ArrayList
        NotUndoable    = New-Object System.Collections.ArrayList
    }
    Save-SessionFile
    $report = $Script:Session

    if (-not $User) { throw "-UndoUser is required with -UndoFrom." }
    if (-not (Test-Path $From)) { throw "Session record not found: $From" }
    $record = Get-Content -Path $From -Raw | ConvertFrom-Json
    $runs = @($record.Runs | Where-Object { -not $_.TestMode -and $_.SamAccountName -eq $User })
    $entryCount = @($runs | ForEach-Object { @($_.Journal) }).Count
    if (-not $runs -or $entryCount -eq 0) {
        Write-GuiLog "No real changes were recorded for $User in that session - nothing to undo."
        $report.CompletedAt = (Get-Date).ToString('o'); Save-SessionFile
        return
    }
    Write-GuiLog "Undoing $entryCount recorded change(s) for $User from $($runs.Count) run(s)..."

    Initialize-AD
    # Identify the account by GUID string, not by an ADUser object: undoing MoveOU changes its DN,
    # and a cached object would keep pointing at the old one for every later entry.
    $adUser = [string](Get-ADUser -Identity $runs[0].ObjectGuid -Server $Script:DC @Script:AdCredParams -ErrorAction Stop).ObjectGUID
    $adSteps = 'DisableAD', 'StampDescription', 'HideGAL_AD', 'RemoveGroups', 'MoveOU'
    $adTouched = $false

    $ErrorActionPreference = 'Stop'
    try {
        for ($r = $runs.Count - 1; $r -ge 0; $r--) {
            $run = $runs[$r]
            $entries = @($run.Journal)
            for ($i = $entries.Count - 1; $i -ge 0; $i--) {
                $e = $entries[$i]
                if ($e.Data.NotUndoable) {
                    [void]$report.NotUndoable.Add("$($e.Step): $($e.Data.NotUndoable)")
                    Write-GuiLog "$($e.Step): can't be undone - $($e.Data.NotUndoable)" "WARN"
                    continue
                }
                try {
                    $msg = Invoke-UndoEntry -Entry $e -Run $run -AdUser $adUser
                    [void]$report.Undone.Add("$($e.Step): $msg")
                    Write-GuiLog "$($e.Step): $msg"
                    if ($adSteps -contains $e.Step) { $adTouched = $true }
                }
                catch {
                    $err = Get-ErrorText $_
                    [void]$report.Failed.Add("$($e.Step): $err")
                    Write-GuiLog "$($e.Step) undo FAILED: $err" "ERROR"
                }
                Save-SessionFile
            }
        }
        if ($adTouched) {
            try { Invoke-DeltaSync; [void]$report.Undone.Add("DeltaSync: delta sync triggered so the cloud picks up the restored AD account."); Write-GuiLog "Delta sync triggered." }
            catch { [void]$report.Failed.Add("DeltaSync: $(Get-ErrorText $_)"); Write-GuiLog "Delta sync failed: $(Get-ErrorText $_)" "ERROR" }
        }
    }
    finally {
        $ErrorActionPreference = 'Continue'
        $report.CompletedAt = (Get-Date).ToString('o')
        Save-SessionFile
    }
}

if ($UndoFrom) {
    $undoForm = New-Object System.Windows.Forms.Form
    $undoForm.Text = "CONTOSO Offboarding - Undo ($UndoUser)"
    $undoForm.Size = New-Object System.Drawing.Size(760, 520)
    $undoForm.StartPosition = "CenterScreen"
    $undoForm.Font = New-Object System.Drawing.Font("Segoe UI", 9)

    $lblUndo = New-Object System.Windows.Forms.Label
    $lblUndo.Text = "Reversing the offboarding changes recorded for $UndoUser. Sign-in windows may appear for Exchange / Microsoft Graph / SharePoint."
    $lblUndo.Location = New-Object System.Drawing.Point(15, 12); $lblUndo.Size = New-Object System.Drawing.Size(715, 36)

    $Script:LogBox            = New-Object System.Windows.Forms.TextBox
    $Script:LogBox.Multiline  = $true
    $Script:LogBox.ScrollBars = "Vertical"
    $Script:LogBox.ReadOnly   = $true
    $Script:LogBox.Font       = New-Object System.Drawing.Font("Consolas", 9)
    $Script:LogBox.Location   = New-Object System.Drawing.Point(15, 52)
    $Script:LogBox.Size       = New-Object System.Drawing.Size(715, 370)
    $Script:LogBox.Anchor     = 'Top, Bottom, Left, Right'

    $btnUndoClose = New-Object System.Windows.Forms.Button
    $btnUndoClose.Text = "Close"; $btnUndoClose.Enabled = $false
    $btnUndoClose.Location = New-Object System.Drawing.Point(630, 432); $btnUndoClose.Size = New-Object System.Drawing.Size(100, 30)
    $btnUndoClose.Anchor = 'Bottom, Right'
    $btnUndoClose.Add_Click({ $undoForm.Close() })

    $undoForm.Controls.AddRange(@($lblUndo, $Script:LogBox, $btnUndoClose))
    $undoForm.Add_FormClosing({ if ($Script:Running) { $_.Cancel = $true } })
    $undoForm.Add_Shown({
        $Script:Running = $true
        if (-not (Test-Path $Script:LogPath)) { New-Item -Path $Script:LogPath -ItemType Directory -Force | Out-Null }
        $undoLog = Join-Path $Script:LogPath "Undo_$($UndoUser)_$(Get-Date -Format 'yyyyMMdd_HHmmss').log"
        try { Start-Transcript -Path $undoLog -Append | Out-Null; $Script:Transcripting = $true } catch { }
        try {
            Invoke-OffboardUndo -From $UndoFrom -User $UndoUser
            $s = $Script:Session
            Write-GuiLog "=== UNDO FINISHED: $($s.Undone.Count) restored, $($s.Failed.Count) failed, $($s.NotUndoable.Count) can't be undone ==="
        }
        catch { Write-GuiLog "Undo stopped: $(Get-ErrorText $_)" "ERROR"; [void]$Script:Session.Failed.Add("Undo: $(Get-ErrorText $_)"); Save-SessionFile }
        finally {
            if ($Script:Transcripting) { try { Stop-Transcript | Out-Null } catch { } }
            $Script:Transcripting = $false
            $Script:Running = $false
            $btnUndoClose.Enabled = $true
        }
    })
    [void]$undoForm.ShowDialog()
    return
}
#endregion

#region ================= BUILD FORM =================
$form                 = New-Object System.Windows.Forms.Form
$form.Text            = "CONTOSO User Offboarding Tool v2"
$form.Size            = New-Object System.Drawing.Size(860, 1010)
$form.MinimumSize     = New-Object System.Drawing.Size(700, 600)
$form.StartPosition   = "CenterScreen"
$form.FormBorderStyle = "Sizable"
$form.MaximizeBox     = $true
$form.AutoScaleMode   = [System.Windows.Forms.AutoScaleMode]::Dpi
$form.AutoScroll      = $true
$font  = New-Object System.Drawing.Font("Segoe UI", 9)
$fontB = New-Object System.Drawing.Font("Segoe UI", 9, [System.Drawing.FontStyle]::Bold)
$form.Font = $font

# ---- Search group ----
$grpSearch          = New-Object System.Windows.Forms.GroupBox
$grpSearch.Text     = "1. Find the user (partial names OK - e.g. 'mike', 'smi', 'j.smith@')"
$grpSearch.Location = New-Object System.Drawing.Point(15, 10)
$grpSearch.Size     = New-Object System.Drawing.Size(775, 215)
$grpSearch.Anchor   = 'Top, Left, Right'

$txtSearch          = New-Object System.Windows.Forms.TextBox
$txtSearch.Location = New-Object System.Drawing.Point(15, 25)
$txtSearch.Size     = New-Object System.Drawing.Size(280, 24)

$btnSearch          = New-Object System.Windows.Forms.Button
$btnSearch.Text     = "Search AD"
$btnSearch.Location = New-Object System.Drawing.Point(305, 23)
$btnSearch.Size     = New-Object System.Drawing.Size(90, 27)

$btnLoad          = New-Object System.Windows.Forms.Button
$btnLoad.Text     = "Load Selected User"
$btnLoad.Location = New-Object System.Drawing.Point(405, 23)
$btnLoad.Size     = New-Object System.Drawing.Size(140, 27)
$btnLoad.Enabled  = $false

$lstResults               = New-Object System.Windows.Forms.ListView
$lstResults.View          = 'Details'
$lstResults.FullRowSelect = $true
$lstResults.MultiSelect   = $false
$lstResults.Location      = New-Object System.Drawing.Point(15, 58)
$lstResults.Size          = New-Object System.Drawing.Size(745, 115)
$lstResults.Anchor        = 'Top, Left, Right'
[void]$lstResults.Columns.Add("Name", 170)
[void]$lstResults.Columns.Add("Username", 110)
[void]$lstResults.Columns.Add("Email", 220)
[void]$lstResults.Columns.Add("Enabled", 60)
[void]$lstResults.Columns.Add("OU", 175)

$lblSelected           = New-Object System.Windows.Forms.Label
$lblSelected.Text      = "No user selected. Search, then double-click a result to load it."
$lblSelected.Location  = New-Object System.Drawing.Point(15, 180)
$lblSelected.Size      = New-Object System.Drawing.Size(745, 25)
$lblSelected.ForeColor = [System.Drawing.Color]::DimGray

$grpSearch.Controls.AddRange(@($txtSearch, $btnSearch, $btnLoad, $lstResults, $lblSelected))

# ---- Tabs of steps (auto-built from registry) ----
$tabs          = New-Object System.Windows.Forms.TabControl
$tabs.Location = New-Object System.Drawing.Point(15, 235)
$tabs.Size     = New-Object System.Drawing.Size(775, 330)
$tabs.Anchor   = 'Top, Left, Right'

$Script:StepCheckboxes = @{}
foreach ($tabName in ($Script:Steps.Tab | Select-Object -Unique)) {
    $page = New-Object System.Windows.Forms.TabPage
    $page.Text = $tabName
    $page.AutoScroll = $true
    $y = 15
    foreach ($step in ($Script:Steps | Where-Object { $_.Tab -eq $tabName })) {
        $chk = New-Object System.Windows.Forms.CheckBox
        $chk.Text     = $step.Text + $(if ($step.NeedsMgr) { "  [needs manager]" } else { "" })
        $chk.Location = New-Object System.Drawing.Point(15, $y)
        $chk.AutoSize = $true
        $chk.Checked  = [bool]$step.Default
        $page.Controls.Add($chk)
        $Script:StepCheckboxes[$step.Id] = $chk
        $y += 26
    }
    # Extra controls that belong on specific tabs - placed below the last checkbox so
    # adding steps to the registry never overlaps them.
    if ($tabName -eq 'Active Directory') {
        $y += 12
        $lblOU = New-Object System.Windows.Forms.Label
        $lblOU.Text = "Disabled Users OU path:"
        $lblOU.Location = New-Object System.Drawing.Point(15, ($y + 3)); $lblOU.AutoSize = $true
        $Script:TxtOU = New-Object System.Windows.Forms.TextBox
        $Script:TxtOU.Location = New-Object System.Drawing.Point(160, $y)
        $Script:TxtOU.Size = New-Object System.Drawing.Size(560, 24)
        $Script:TxtOU.Text = $Script:DefaultDisabledOU
        $page.Controls.AddRange(@($lblOU, $Script:TxtOU))
    }
    $tabs.TabPages.Add($page)
}

# ---- Options row: preset, manager, ticket, test mode ----
$lblPreset          = New-Object System.Windows.Forms.Label
$lblPreset.Text     = "Preset:"
$lblPreset.Location = New-Object System.Drawing.Point(15, 578); $lblPreset.AutoSize = $true

$cboPreset               = New-Object System.Windows.Forms.ComboBox
$cboPreset.DropDownStyle = 'DropDownList'
$cboPreset.Location      = New-Object System.Drawing.Point(65, 575)
$cboPreset.Size          = New-Object System.Drawing.Size(170, 24)
$Script:Presets.Keys | ForEach-Object { [void]$cboPreset.Items.Add($_) }

$lblMgr          = New-Object System.Windows.Forms.Label
$lblMgr.Text     = "Manager UPN/email:"
$lblMgr.Location = New-Object System.Drawing.Point(255, 578); $lblMgr.AutoSize = $true

$txtMgr          = New-Object System.Windows.Forms.TextBox
$txtMgr.Location = New-Object System.Drawing.Point(380, 575)
$txtMgr.Size     = New-Object System.Drawing.Size(220, 24)

$lblTicket          = New-Object System.Windows.Forms.Label
$lblTicket.Text     = "Ticket #:"
$lblTicket.Location = New-Object System.Drawing.Point(615, 578); $lblTicket.AutoSize = $true

$txtTicket          = New-Object System.Windows.Forms.TextBox
$txtTicket.Location = New-Object System.Drawing.Point(672, 575)
$txtTicket.Size     = New-Object System.Drawing.Size(115, 24)

$chkTestMode           = New-Object System.Windows.Forms.CheckBox
$chkTestMode.Text      = "TEST MODE - log what would happen, change nothing"
$chkTestMode.Location  = New-Object System.Drawing.Point(15, 610)
$chkTestMode.AutoSize  = $true
$chkTestMode.ForeColor = [System.Drawing.Color]::FromArgb(230, 81, 0)
$chkTestMode.Font      = $fontB
$chkTestMode.Checked   = $true   # default ON: staff must consciously turn it off

$btnRun           = New-Object System.Windows.Forms.Button
$btnRun.Text      = "Run"
$btnRun.Location  = New-Object System.Drawing.Point(540, 605)
$btnRun.Size      = New-Object System.Drawing.Size(140, 32)
$btnRun.Font      = $fontB
$btnRun.BackColor = [System.Drawing.Color]::FromArgb(198, 40, 40)
$btnRun.ForeColor = [System.Drawing.Color]::White
$btnRun.Enabled   = $false
$btnRun.Anchor    = 'Top, Right'

$btnClose          = New-Object System.Windows.Forms.Button
$btnClose.Text     = "Close"
$btnClose.Location = New-Object System.Drawing.Point(690, 605)
$btnClose.Size     = New-Object System.Drawing.Size(100, 32)
$btnClose.Anchor   = 'Top, Right'

$btnCred          = New-Object System.Windows.Forms.Button
$btnCred.Text     = "Saved Credential..."
$btnCred.Location = New-Object System.Drawing.Point(15, 640)
$btnCred.Size     = New-Object System.Drawing.Size(150, 26)
$btnCred.Anchor   = 'Top, Left'

# ---- Log ----
$Script:LogBox            = New-Object System.Windows.Forms.TextBox
$Script:LogBox.Multiline  = $true
$Script:LogBox.ScrollBars = "Vertical"
$Script:LogBox.ReadOnly   = $true
$Script:LogBox.Font       = New-Object System.Drawing.Font("Consolas", 9)
$Script:LogBox.Location   = New-Object System.Drawing.Point(15, 675)
$Script:LogBox.Size       = New-Object System.Drawing.Size(775, 220)
$Script:LogBox.Anchor     = 'Top, Bottom, Left, Right'

$form.Controls.AddRange(@($grpSearch, $tabs, $lblPreset, $cboPreset, $lblMgr, $txtMgr, $lblTicket, $txtTicket, $chkTestMode, $btnRun, $btnClose, $btnCred, $Script:LogBox))

# Everything the operator could change mid-run (DoEvents keeps the UI live while steps run).
$Script:RunLockedControls = @($grpSearch, $tabs, $cboPreset, $txtMgr, $txtTicket, $chkTestMode, $btnRun, $btnClose, $btnCred)
#endregion

#region ================= SEARCH / SELECT HANDLERS =================
$btnSearch.Add_Click({
    $lstResults.Items.Clear()
    $Script:TargetUser = $null
    $btnRun.Enabled = $false
    $term = $txtSearch.Text.Trim()
    if ($term.Length -lt 2) {
        [System.Windows.Forms.MessageBox]::Show("Type at least 2 characters to search.", "Search", 'OK', 'Warning') | Out-Null
        return
    }
    try {
        Initialize-AD
        # Escape LDAP special chars in the term
        $safe = $term -replace '\\', '\5c' -replace '\*', '\2a' -replace '\(', '\28' -replace '\)', '\29'
        # ANR (ambiguous name resolution) covers first/last/display/sam prefix matching;
        # the OR-filter adds substring matching on name, sam, and mail.
        $ldap = "(&(objectCategory=person)(objectClass=user)(|(anr=$safe)(cn=*$safe*)(sAMAccountName=*$safe*)(mail=*$safe*)))"
        $all = @(Get-ADUser -LDAPFilter $ldap -Server $Script:DC -Properties mail, Enabled, DistinguishedName, DisplayName @Script:AdCredParams)
        if (-not $all) {
            $lblSelected.Text = "No matches for '$term'. Try fewer letters or a different fragment."
            $lblSelected.ForeColor = [System.Drawing.Color]::Firebrick
            return
        }
        $found = $all | Sort-Object Name | Select-Object -First $Script:MaxSearchResults
        foreach ($f in $found) {
            $ou = ($f.DistinguishedName -split ',', 2)[1]
            $item = New-Object System.Windows.Forms.ListViewItem($f.Name)
            [void]$item.SubItems.Add($f.SamAccountName)
            [void]$item.SubItems.Add([string]$f.mail)
            [void]$item.SubItems.Add($(if ($f.Enabled) { "Yes" } else { "No" }))
            [void]$item.SubItems.Add($ou)
            $item.Tag = $f.SamAccountName
            if (-not $f.Enabled) { $item.ForeColor = [System.Drawing.Color]::Gray }
            [void]$lstResults.Items.Add($item)
        }
        $shown = @($found).Count
        $lblSelected.Text = if ($all.Count -gt $shown) {
            "Showing the first $shown of $($all.Count) matches - type more to narrow it down. Double-click the right one to load it."
        } else { "$shown match(es) shown. Double-click the right one to load it." }
        $lblSelected.ForeColor = [System.Drawing.Color]::DimGray
        Write-GuiLog "Search '$term' returned $($all.Count) result(s)."
    }
    catch {
        $lblSelected.Text = "Search failed: $($_.Exception.Message)"
        $lblSelected.ForeColor = [System.Drawing.Color]::Firebrick
    }
})

# Enter key in the search box triggers search
$txtSearch.Add_KeyDown({ if ($_.KeyCode -eq 'Enter') { $btnSearch.PerformClick(); $_.SuppressKeyPress = $true } })

$lstResults.Add_DoubleClick({
    if ($lstResults.SelectedItems.Count -eq 0) { return }
    Load-SelectedUser
})

# Single-click a row enables the Load button
$lstResults.Add_SelectedIndexChanged({
    $btnLoad.Enabled = ($lstResults.SelectedItems.Count -gt 0)
})

$btnLoad.Add_Click({ Load-SelectedUser })

function Show-LoadedUser {
    param($u)
    $status = if ($u.Enabled) { "ENABLED" } else { "already disabled" }
    $lblSelected.Text = "LOADED: $($u.Name) ($($u.SamAccountName))  |  $($u.mail)  |  $status  |  Groups: $($u.MemberOf.Count)"
    $lblSelected.ForeColor = [System.Drawing.Color]::FromArgb(27, 94, 32)
}

function Load-SelectedUser {
    if ($lstResults.SelectedItems.Count -eq 0) {
        [System.Windows.Forms.MessageBox]::Show("Select a user from the list first.", "Load user", 'OK', 'Warning') | Out-Null
        return
    }
    $sam = $lstResults.SelectedItems[0].Tag
    try {
        $u = Get-ADUser -Identity $sam -Server $Script:DC -Properties $Script:UserProps @Script:AdCredParams
        $Script:TargetUser = $u
        Show-LoadedUser $u
        $btnRun.Enabled = $true
        Write-GuiLog "Loaded user $($u.SamAccountName) ($($u.mail))."
    }
    catch {
        $lblSelected.Text = "Load failed: $($_.Exception.Message)"
        $lblSelected.ForeColor = [System.Drawing.Color]::Firebrick
    }
}

$cboPreset.Add_SelectedIndexChanged({
    $ids = $Script:Presets[$cboPreset.SelectedItem]
    foreach ($kv in $Script:StepCheckboxes.GetEnumerator()) { $kv.Value.Checked = ($ids -contains $kv.Key) }
    Write-GuiLog "Preset applied: $($cboPreset.SelectedItem)"
})
#endregion

#region ================= RUN HANDLER =================
$btnRun.Add_Click({
    if (-not $Script:TargetUser) { return }
    $dryRun = $chkTestMode.Checked
    $mgr = $txtMgr.Text.Trim()
    $ticket = $txtTicket.Text.Trim()

    $selected = @($Script:Steps | Where-Object { $Script:StepCheckboxes[$_.Id].Checked })
    if (-not $selected) {
        [System.Windows.Forms.MessageBox]::Show("No actions selected.", "Offboarding", 'OK', 'Warning') | Out-Null
        return
    }
    if (($selected | Where-Object { $_.NeedsMgr }) -and -not $mgr) {
        [System.Windows.Forms.MessageBox]::Show("One or more selected actions need the Manager field filled in.", "Missing manager", 'OK', 'Warning') | Out-Null
        return
    }
    if (-not $dryRun -and -not $ticket) {
        [System.Windows.Forms.MessageBox]::Show("Enter the ticket # before a real (non-test) run.", "Missing ticket", 'OK', 'Warning') | Out-Null
        return
    }
    $ids = $selected.Id
    if ($ids -contains 'ArchiveOneDrive' -and $ids -contains 'OneDriveAccess') {
        $ans = [System.Windows.Forms.MessageBox]::Show("'Archive OneDrive' moves the files out (the OneDrive ends up empty), so granting the manager OneDrive access afterwards gives them an empty OneDrive.`n`nContinue anyway?", "OneDrive options conflict", 'YesNo', 'Warning', 'Button2')
        if ($ans -ne 'Yes') { return }
    }

    # Re-read the user so group lists / enabled state aren't stale from an earlier load or run.
    try {
        $u = Get-ADUser -Identity $Script:TargetUser.SamAccountName -Server $Script:DC -Properties $Script:UserProps @Script:AdCredParams
        $Script:TargetUser = $u
        Show-LoadedUser $u
    }
    catch {
        [System.Windows.Forms.MessageBox]::Show("Couldn't re-read $($Script:TargetUser.SamAccountName) from AD: $($_.Exception.Message)", "Offboarding", 'OK', 'Error') | Out-Null
        return
    }

    $modeLabel = if ($dryRun) { "TEST MODE - nothing will actually change.`n`n" } else { "" }
    $confirmText = $modeLabel + "Target:`n    $($u.Name)  ($($u.SamAccountName))`n`nActions (in this order):`n - " + (($selected.Text) -join "`n - ") + "`n`nProceed?"
    $title = if ($dryRun) { "Confirm TEST RUN" } else { "Confirm offboarding" }
    if ([System.Windows.Forms.MessageBox]::Show($confirmText, $title, 'YesNo', 'Warning', 'Button2') -ne 'Yes') {
        Write-GuiLog "Cancelled by operator." "WARN"; return
    }

    if (-not (Test-Path $Script:LogPath)) { New-Item -Path $Script:LogPath -ItemType Directory -Force | Out-Null }
    $started     = Get-Date
    $timestamp   = $started.ToString("yyyyMMdd_HHmmss")
    $transcript  = Join-Path $Script:LogPath "Offboard_$($u.SamAccountName)_$timestamp.log"
    $summaryPath = Join-Path $Script:LogPath "Offboard_$($u.SamAccountName)_$timestamp.csv"

    $errors = New-Object System.Collections.Generic.List[string]
    $stepResults = [ordered]@{}
    $Script:Running = $true
    foreach ($c in $Script:RunLockedControls) { $c.Enabled = $false }
    try {
        try { Start-Transcript -Path $transcript -Append | Out-Null; $Script:Transcripting = $true }
        catch { Write-GuiLog "Transcript not started: $($_.Exception.Message)" "WARN" }

        # Failed cmdlets must fail their step, not get logged as success. (EXO module
        # cmdlets ignore this preference - steps pass -ErrorAction Stop to those explicitly.)
        $ErrorActionPreference = 'Stop'

        # This run's entry in the session record (-ResultFile). Added BEFORE any step runs and saved
        # after every step, so if the tool crashes or is killed mid-run, the undo journal still
        # covers every change that had already been made.
        $journal  = New-Object System.Collections.ArrayList
        $runSteps = New-Object System.Collections.ArrayList
        $run = [ordered]@{
            SamAccountName    = $u.SamAccountName
            UserPrincipalName = $u.UserPrincipalName
            Mail              = [string]$u.mail
            ObjectGuid        = [string]$u.ObjectGUID
            TestMode          = $dryRun
            Ticket            = $ticket
            Operator          = $Script:OperatorName
            StartedAt         = $started.ToString('o')
            CompletedAt       = $null
            ErrorCount        = 0
            Steps             = $runSteps
            Journal           = $journal
        }
        [void]$Script:Session.Runs.Add($run)
        Save-SessionFile

        # Context handed to every step
        $ctx = @{
            User = $u; Upn = $u.UserPrincipalName; Mail = $(if ($u.mail) { $u.mail } else { $u.UserPrincipalName })
            Mgr = $mgr; Ticket = $ticket
            DryRun = $dryRun; Timestamp = $timestamp; Results = $stepResults
            CurrentStep = $null
            Log = { param($m, $l = "INFO") Write-GuiLog $m $l }
            Do  = { param([string]$What, [scriptblock]$Work)
                    if ($dryRun) { Write-GuiLog "WOULD: $What" "TEST" }
                    else { & $Work; Write-GuiLog $What } }
            Record = { param($Data) if (-not $dryRun) { [void]$journal.Add([ordered]@{ Step = $ctx.CurrentStep; Data = $Data }) } }
        }

        Write-GuiLog "=== Offboarding $($u.SamAccountName) started (ticket: $ticket, test mode: $dryRun, as: $($Script:OperatorName)) ==="

        foreach ($step in $selected) {
            Write-GuiLog "--- Step: $($step.Id) ---"
            $ctx.CurrentStep = $step.Id
            $stepError = $null
            try {
                & $step.Action $ctx
                $stepResults[$step.Id] = if ($dryRun) { "Simulated" } else { "OK" }
            }
            catch {
                $stepError = Get-ErrorText $_
                $stepResults[$step.Id] = "FAILED"
                $errors.Add("$($step.Id): $stepError")
                Write-GuiLog "$($step.Id) failed: $stepError" "ERROR"
            }
            [void]$runSteps.Add([ordered]@{ Id = $step.Id; Result = $stepResults[$step.Id]; Error = $stepError })
            $run.ErrorCount = $errors.Count
            Save-SessionFile
        }
        $ErrorActionPreference = 'Continue'
        $run.CompletedAt = (Get-Date).ToString('o')
        Save-SessionFile

        $summary = [ordered]@{
            Identity = $u.SamAccountName; Operator = $Script:OperatorName; Ticket = $ticket
            TestMode = $dryRun; Started = $started; Finished = Get-Date; ErrorCount = $errors.Count
            Errors = ($errors -join ' | ')
        }
        foreach ($k in $stepResults.Keys) { $summary["Step_$k"] = $stepResults[$k] }
        try { [PSCustomObject]$summary | Export-Csv -Path $summaryPath -NoTypeInformation }
        catch { Write-GuiLog "Could not write summary CSV: $($_.Exception.Message)" "WARN" }

        if ($dryRun) {
            Write-GuiLog "=== TEST RUN COMPLETE - no changes were made ($($errors.Count) step(s) would have failed) ===" "TEST"
            [System.Windows.Forms.MessageBox]::Show("Test run complete for $($u.Name).`n$($errors.Count) step(s) reported problems.`nLog: $transcript", "Test run done", 'OK', 'Information') | Out-Null
        }
        elseif ($errors.Count -eq 0) {
            Write-GuiLog "=== COMPLETE - all selected actions succeeded ==="
            [System.Windows.Forms.MessageBox]::Show("Offboarding complete for $($u.Name).`nLog: $transcript", "Done", 'OK', 'Information') | Out-Null
        }
        else {
            Write-GuiLog "=== COMPLETE with $($errors.Count) error(s) - review the log ===" "WARN"
            [System.Windows.Forms.MessageBox]::Show("Finished with $($errors.Count) error(s):`n - $($errors -join "`n - ")`n`nLog: $transcript", "Completed with errors", 'OK', 'Warning') | Out-Null
        }
    }
    catch {
        Write-GuiLog "Run aborted unexpectedly: $(Get-ErrorText $_)" "ERROR"
    }
    finally {
        $ErrorActionPreference = 'Continue'
        if ($Script:Transcripting) { try { Stop-Transcript | Out-Null } catch { } }
        $Script:Transcripting = $false
        $Script:Running = $false
        foreach ($c in $Script:RunLockedControls) { $c.Enabled = $true }
    }
})
#endregion

$btnClose.Add_Click({ $form.Close() })

# Don't let the window close while steps are still running (DoEvents keeps the X button live).
$form.Add_FormClosing({
    if ($Script:Running) {
        $_.Cancel = $true
        [System.Windows.Forms.MessageBox]::Show("Offboarding is still running - wait for it to finish.", "Busy", 'OK', 'Warning') | Out-Null
    }
})

$btnCred.Add_Click({
    $hasCM = Test-ModuleAvailable 'CredentialManager'
    $existing = if ($hasCM) {
        Import-Module CredentialManager -ErrorAction SilentlyContinue
        Get-StoredCredential -Target $Script:CredTarget -ErrorAction SilentlyContinue
    } else { $null }

    $status = if (-not $hasCM) { "CredentialManager module not installed - saving is unavailable.`n(Install with: Install-Module CredentialManager)" }
              elseif ($existing) { "Currently saved: $($existing.UserName)" }
              else { "No credential currently saved under '$($Script:CredTarget)'." }

    $msg = "$status`n`nYes = set / replace the saved credential`nNo = clear the saved credential`nCancel = do nothing"
    $choice = [System.Windows.Forms.MessageBox]::Show($msg, "Saved offboarding credential", 'YesNoCancel', 'Question')

    if ($choice -eq 'Yes') {
        if (-not $hasCM) { [System.Windows.Forms.MessageBox]::Show("Install the CredentialManager module first.", "Unavailable", 'OK', 'Warning') | Out-Null; return }
        $cred = Get-Credential -Message "Enter the Microsoft 365 admin sign-in to save for '$($Script:CredTarget)' (name@contoso.com - NOT DOMAIN\user)."
        if ($cred -and $cred.UserName -notlike '*@*') {
            [System.Windows.Forms.MessageBox]::Show("'$($cred.UserName)' isn't a Microsoft 365 sign-in. Use the name@contoso.com form - Exchange Online rejects DOMAIN\user.", "Not saved", 'OK', 'Warning') | Out-Null
            return
        }
        if ($cred) {
            try {
                New-StoredCredential -Target $Script:CredTarget -UserName $cred.UserName `
                    -Password $cred.GetNetworkCredential().Password -Persist LocalMachine -Type Generic -ErrorAction Stop | Out-Null
                $Script:Cred = $cred
                Write-GuiLog "Credential '$($Script:CredTarget)' saved ($($cred.UserName))."
            }
            catch { Write-GuiLog "Failed to save credential: $($_.Exception.Message)" "ERROR" }
        }
    }
    elseif ($choice -eq 'No') {
        if ($hasCM -and $existing) {
            try { Remove-StoredCredential -Target $Script:CredTarget -ErrorAction Stop; $Script:Cred = $null; Write-GuiLog "Saved credential cleared." }
            catch { Write-GuiLog "Failed to clear credential: $($_.Exception.Message)" "ERROR" }
        } else { Write-GuiLog "Nothing to clear." }
    }
})

Write-GuiLog "CONTOSO Offboarding Tool v2 ready. Search for a user to begin. TEST MODE is ON by default."
if ($Script:AdCredParams.Credential) { Write-GuiLog "AD changes will be made as $($Script:OperatorName) (handed over by Inventory Manager)." }
if ($Script:UseStoredCred) {
    if (Test-ModuleAvailable 'CredentialManager') {
        Write-GuiLog "Credential mode: Windows Credential Manager (target '$($Script:CredTarget)'). Use 'Saved Credential...' to set or clear it."
    } else {
        Write-GuiLog "CredentialManager module not installed - EXO will prompt per session. Install-Module CredentialManager to enable saved credentials." "WARN"
    }
}
if (-not [string]::IsNullOrWhiteSpace($PrefillSearch)) {
    $txtSearch.Text = $PrefillSearch
    # PerformClick() silently does nothing if called before the button has a live window handle
    # (confirmed by reproduction - the Click event never fires, not even deferred) - the handle
    # doesn't exist until the form is actually shown, so this has to run from Add_Shown, not here.
    $form.Add_Shown({ $btnSearch.PerformClick() })
}

# The session record exists from the moment the tool is up, so the caller can tell "opened but
# nothing was run" apart from "never started" (e.g. blocked by the signature check).
Save-SessionFile
$form.Add_FormClosed({ $Script:Session.ClosedAt = (Get-Date).ToString('o'); Save-SessionFile })

[void]$form.ShowDialog()

