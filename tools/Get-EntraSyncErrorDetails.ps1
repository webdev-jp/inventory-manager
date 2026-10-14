<#
.SYNOPSIS
    READ-ONLY. Collects what Entra knows about the 4 cloud objects behind the 2026-09-30/10-01
    Entra Connect export errors, so the right fix can be chosen. Changes nothing.

    Signs in with a device code: open the URL it prints, enter the code, and sign in with an
    account that can read the directory and audit logs (e.g. Global Reader). Writes the result to
    tools\entra-sync-error-details.txt next to this script.

    Uses only Microsoft.Graph.Authentication (Invoke-MgGraphRequest), because this machine has
    mixed 2.37/2.38 Graph module versions that conflict when loaded together.
#>
$ErrorActionPreference = 'Continue'
Import-Module Microsoft.Graph.Authentication
$out = Join-Path $PSScriptRoot 'entra-sync-error-details.txt'
Start-Transcript -Path $out -Force | Out-Null

function Get-Graph([string]$Uri, [hashtable]$Headers = @{}) {
    try { Invoke-MgGraphRequest -Method GET -Uri $Uri -Headers $Headers -OutputType PSObject -ErrorAction Stop }
    catch { Write-Host "   (request failed: $($_.Exception.Message))"; $null }
}

Connect-MgGraph -Scopes 'User.Read.All','Directory.Read.All','RoleManagement.Read.Directory','AuditLog.Read.All' -UseDeviceCode -NoWelcome

$objects = [ordered]@{
    'teamsadmin cloud admin'     = 'ca073c2e-df06-40c3-9cce-1a8e41bf3bbb'
    'SPAdmin cloud admin'        = '83df302e-076c-43db-9478-e76d25fc562e'
    'TestUser - object in error' = 'dadec421-f526-45fb-850e-419cfcc89ccb'
    'TestUser - conflicting obj' = '059b98f7-9181-4c7f-9338-9b06a9588238'
}
$select = 'id,displayName,userPrincipalName,mail,proxyAddresses,accountEnabled,createdDateTime,userType,' +
          'onPremisesSyncEnabled,onPremisesImmutableId,onPremisesSamAccountName,onPremisesSecurityIdentifier,' +
          'onPremisesLastSyncDateTime,onPremisesDistinguishedName,assignedLicenses'

foreach ($label in $objects.Keys) {
    $id = $objects[$label]
    Write-Host "`n==================== $label ($id) ===================="
    $u = Get-Graph "v1.0/users/$id`?`$select=$select"
    if ($u) {
        $u | Select-Object id, displayName, userPrincipalName, mail, accountEnabled, createdDateTime, userType,
            onPremisesSyncEnabled, onPremisesImmutableId, onPremisesSamAccountName, onPremisesSecurityIdentifier,
            onPremisesLastSyncDateTime, onPremisesDistinguishedName,
            @{n='proxyAddresses';e={$_.proxyAddresses -join '; '}},
            @{n='licenseCount';e={@($_.assignedLicenses).Count}} |
            Format-List | Out-String -Width 300 | Write-Host
        Write-Host '-- Directory roles (active assignments):'
        $roles = Get-Graph "v1.0/roleManagement/directory/roleAssignments?`$filter=principalId eq '$id'&`$expand=roleDefinition"
        if ($roles) { $roles.value | ForEach-Object { "   $($_.roleDefinition.displayName)" } | Write-Host; if (-not $roles.value) { Write-Host '   (none)' } }
    } else {
        Write-Host '-- Not an active user. Checking deleted users and other object types:'
        $d = Get-Graph "v1.0/directory/deletedItems/$id"
        if ($d) { $d | Format-List | Out-String -Width 300 | Write-Host }
        $o = Get-Graph "v1.0/directoryObjects/$id"
        if ($o) { $o | Format-List | Out-String -Width 300 | Write-Host }
    }
    Write-Host '-- Audit log since 2026-09-29:'
    $audit = Get-Graph "v1.0/auditLogs/directoryAudits?`$filter=activityDateTime ge 2026-09-29T00:00:00Z and targetResources/any(t:t/id eq '$id')"
    if ($audit) {
        $audit.value | Sort-Object activityDateTime | ForEach-Object {
            "   {0}  {1}  by {2}{3}" -f $_.activityDateTime, $_.activityDisplayName, $_.initiatedBy.user.userPrincipalName, $_.initiatedBy.app.displayName
        } | Write-Host
        if (-not $audit.value) { Write-Host '   (no entries)' }
    }
}

Write-Host "`n==================== Every object holding TestUser's SMTP address ===================="
$smtp = Get-Graph "v1.0/users?`$filter=proxyAddresses/any(p:p eq 'SMTP:TestUserA@contoso.com') or proxyAddresses/any(p:p eq 'smtp:TestUserA@contoso.com')&`$select=id,displayName,userPrincipalName,onPremisesSyncEnabled&`$count=true" @{ ConsistencyLevel = 'eventual' }
if ($smtp) { $smtp.value | Format-Table id, displayName, userPrincipalName, onPremisesSyncEnabled -AutoSize | Out-String -Width 300 | Write-Host }
$contacts = Get-Graph "v1.0/contacts?`$filter=proxyAddresses/any(p:p eq 'SMTP:TestUserA@contoso.com')&`$select=id,displayName,mail&`$count=true" @{ ConsistencyLevel = 'eventual' }
if ($contacts -and $contacts.value) { Write-Host 'Contacts:'; $contacts.value | Format-Table id, displayName, mail -AutoSize | Out-String | Write-Host }

Write-Host "`n==================== Tenant sync feature flags ===================="
$sync = Get-Graph 'v1.0/directory/onPremisesSynchronization'
if ($sync) { $sync.value | ForEach-Object { $_.features | Format-List | Out-String | Write-Host } }

Disconnect-MgGraph | Out-Null
Stop-Transcript | Out-Null
Write-Host "`nSaved to $out" -ForegroundColor Green
