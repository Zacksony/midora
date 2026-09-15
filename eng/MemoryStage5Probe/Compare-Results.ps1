[CmdletBinding()]
param([Parameter(Mandatory)][string]$Baseline, [Parameter(Mandatory)][string]$Candidate)
$ErrorActionPreference = 'Stop'
$old = Get-Content -LiteralPath (Join-Path $Baseline 'result.json') -Raw | ConvertFrom-Json
$new = Get-Content -LiteralPath (Join-Path $Candidate 'result.json') -Raw | ConvertFrom-Json
if ($old.schema -ne $new.schema) { throw 'Digest schema differs; do not compare different probe schemas.' }
if (($old.fixture | ConvertTo-Json -Compress) -ne ($new.fixture | ConvertTo-Json -Compress)) { throw 'Fixture mismatch.' }
$oldCanonical = @($old.records | Where-Object phase -Like '*-canonical')
$newCanonical = @($new.records | Where-Object phase -Like '*-canonical')
if ($oldCanonical.Count -ne $newCanonical.Count) { throw 'Canonical phase count mismatch.' }
for ($i = 0; $i -lt $oldCanonical.Count; $i++) {
    if ($oldCanonical[$i].phase -ne $newCanonical[$i].phase -or $oldCanonical[$i].digest -ne $newCanonical[$i].digest) {
        throw "Formal canonical mismatch at $($oldCanonical[$i].phase)."
    }
}
$rows = foreach ($phase in $old.records | Where-Object state -EQ complete) {
    $after = $new.records | Where-Object phase -EQ $phase.phase
    [pscustomobject]@{ Phase = $phase.phase; OldMs = [Math]::Round($phase.elapsedMs, 2); NewMs = [Math]::Round($after.elapsedMs, 2)
        OldAllocatedMiB = [Math]::Round($phase.allocatedBytes / 1MB, 2); NewAllocatedMiB = [Math]::Round($after.allocatedBytes / 1MB, 2)
        OldPrivateMiB = [Math]::Round($phase.privateBytes / 1MB, 2); NewPrivateMiB = [Math]::Round($after.privateBytes / 1MB, 2) }
}
$rows | Format-Table -AutoSize
foreach ($path in @($Baseline, $Candidate)) {
    $guardPath = Join-Path $path 'guard-result.json'
    if (-not (Test-Path -LiteralPath $guardPath)) { $guardPath = Join-Path ($path + '-guard') 'guard-result.json' }
    $guard = Get-Content -LiteralPath $guardPath -Raw | ConvertFrom-Json
    if ($guard.exitCode -ne 0 -or $null -ne $guard.stopReason) { throw "Incomplete/guard-stopped run: $path" }
    Write-Output "$path : peak Private=$([Math]::Round($guard.peakTreePrivateBytes / 1MB, 2)) MiB; peak WS=$([Math]::Round($guard.peakTreeWorkingSetBytes / 1MB, 2)) MiB"
}
Write-Output "Verified $($oldCanonical.Count) exact canonical digests (events, source, diagnostics, context, allocations and Conductor)."
