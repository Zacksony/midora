[CmdletBinding()]
param([Parameter(Mandatory)][string]$Before, [Parameter(Mandatory)][string]$After)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$oldResult = Get-Content -LiteralPath $Before -Raw | ConvertFrom-Json
$newResult = Get-Content -LiteralPath $After -Raw | ConvertFrom-Json
if ($oldResult.kind -ne $newResult.kind -or $oldResult.count -ne $newResult.count) {
    throw 'Only compare the same fixture and count.'
}
$oldHashes = @($oldResult.hashes.PSObject.Properties)
$newHashes = @($newResult.hashes.PSObject.Properties)
if ($oldHashes.Count -ne $newHashes.Count) { throw 'Package entry count changed.' }
foreach ($entry in $oldHashes) {
    $candidate = $newHashes | Where-Object Name -CEQ $entry.Name
    if ($null -eq $candidate -or $entry.Value -cne $candidate.Value) { throw "Package bytes differ at $($entry.Name)." }
}
Write-Output "Fixture: $($oldResult.kind), count: $($oldResult.count); all $($oldHashes.Count) package entry hashes identical."
$rows = foreach ($oldPhase in $oldResult.phases) {
    $newPhase = $newResult.phases | Where-Object phase -EQ $oldPhase.phase
    if ($null -eq $newPhase) { throw "Missing phase: $($oldPhase.phase)" }
    [pscustomobject]@{
        Phase = $oldPhase.phase
        OldMs = [Math]::Round($oldPhase.milliseconds, 1)
        NewMs = [Math]::Round($newPhase.milliseconds, 1)
        ChangePercent = [Math]::Round(100 * ($newPhase.milliseconds / $oldPhase.milliseconds - 1), 1)
        OldAllocatedMiB = [Math]::Round($oldPhase.allocatedBytes / 1MB, 2)
        NewAllocatedMiB = [Math]::Round($newPhase.allocatedBytes / 1MB, 2)
    }
}
$rows | Format-Table -AutoSize
Write-Output ('Peak working set: {0:F2} -> {1:F2} MiB' -f ($oldResult.peakWorkingSetBytes / 1MB), ($newResult.peakWorkingSetBytes / 1MB))
