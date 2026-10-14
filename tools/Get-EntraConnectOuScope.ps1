<#
.SYNOPSIS
    READ-ONLY. Shows which OUs Entra Connect syncs and which it doesn't. Run on the Entra Connect
    server (needs the ADSync module that ships with Connect; does NOT need the AD module).

.DESCRIPTION
    Reads the AD connector's OU filter (the inclusion and exclusion lists set in the Connect wizard
    under "Domain and OU filtering"), then lists every OU in the domain with its effective status:
    an OU inherits from the closest of itself or its parents that appears in either list.

    This covers OU filtering only. Objects can still be skipped by attribute rules (for example
    adminDescription starting with "User_") or by group-based filtering if that was configured.
#>
$ErrorActionPreference = 'Stop'
Import-Module ADSync

$adConnectors = Get-ADSyncConnector | Where-Object { $_.ConnectorTypeName -eq 'AD' }
foreach ($connector in $adConnectors) {
    Write-Host "`n=== AD connector: $($connector.Name) ===" -ForegroundColor Cyan
    foreach ($partition in $connector.Partitions | Where-Object { $_.Selected }) {
        $scope = $partition.ConnectorPartitionScope
        $included = @($scope.ContainerInclusionList)
        $excluded = @($scope.ContainerExclusionList)

        Write-Host "`nPartition: $($partition.DN)"
        Write-Host 'Explicitly INCLUDED:' -ForegroundColor Green
        if ($included) { $included | ForEach-Object { "   $_" } } else { '   (none)' }
        Write-Host 'Explicitly EXCLUDED:' -ForegroundColor Yellow
        if ($excluded) { $excluded | ForEach-Object { "   $_" } } else { '   (none)' }

        $rules = @{}
        foreach ($dn in $included) { $rules[$dn.ToLowerInvariant()] = 'Synced' }
        foreach ($dn in $excluded) { $rules[$dn.ToLowerInvariant()] = 'NOT synced' }

        $searcher = [adsisearcher]'(|(objectClass=organizationalUnit)(objectClass=container))'
        $searcher.SearchRoot = [adsi]"LDAP://$($partition.DN)"
        $searcher.PageSize = 1000
        [void]$searcher.PropertiesToLoad.Add('distinguishedName')
        $containers = $searcher.FindAll() | ForEach-Object { [string]$_.Properties['distinguishedname'][0] }

        $rows = foreach ($dn in $containers) {
            $status = 'NOT synced'
            $from = '(default - no rule above it)'
            $probe = $dn
            while ($probe) {
                if ($rules.ContainsKey($probe.ToLowerInvariant())) {
                    $status = $rules[$probe.ToLowerInvariant()]
                    $from = if ($probe -eq $dn) { 'set on this OU' } else { "inherited from $probe" }
                    break
                }
                $comma = $probe.IndexOf(',')
                $probe = if ($comma -ge 0) { $probe.Substring($comma + 1) } else { $null }
            }
            # Show the path top-down (e.g. Domain Users \ Riverside) so nesting is easy to read.
            $path = (($dn -split '(?<!\\),' | Where-Object { $_ -notmatch '^DC=' } | ForEach-Object { $_ -replace '^(OU|CN)=' }) -join ' \ ')
            $parts = $path -split ' \\ '
            [array]::Reverse($parts)
            [pscustomobject]@{ Status = $status; OU = ($parts -join ' \ '); Why = $from }
        }

        Write-Host "`nEffective status of every OU/container:"
        $rows | Sort-Object OU | Format-Table -AutoSize -Wrap | Out-String -Width 250 | Write-Host
        $synced = @($rows | Where-Object Status -eq 'Synced').Count
        Write-Host "$synced synced, $($rows.Count - $synced) not synced." -ForegroundColor Cyan
    }
}
