[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BaselineDirectory,
    [Parameter(Mandatory)][string]$CandidateDirectory
)
$ErrorActionPreference = 'Stop'
function Read-Result([string]$directory) {
    $lines = @(Get-Content -LiteralPath (Join-Path $directory 'result.jsonl') | ForEach-Object { $_ | ConvertFrom-Json })
    if ($lines.Count -lt 8 -or $lines[0].schema -ne 1) { throw 'Missing or incompatible probe result.' }
    $last = $lines[-1]
    if ($last.phase -ne 'closed-controlled-gc' -or $last.alive -ne 0) { throw 'Incomplete probe or failed close verification.' }
    $guard = Get-Content -LiteralPath (Join-Path ($directory + '-guard') 'guard-result.json') -Raw | ConvertFrom-Json
    if ($guard.exitCode -ne 0 -or $null -ne $guard.stopReason -or $guard.limitBytes -gt 8589934592 -or $guard.reserveBytes -lt 2147483648) {
        throw 'Unsuccessful or insufficiently guarded result.'
    }
    return @{ Lines = $lines; Header = $lines[0]; Guard = $guard }
}
$old = Read-Result $BaselineDirectory
$new = Read-Result $CandidateDirectory
foreach ($field in @('scenario', 'count', 'templateNotes', 'loops', 'voices', 'runtime')) {
    if ($old.Header.$field -ne $new.Header.$field) { throw "Fixture mismatch: $field" }
}
foreach ($phase in @('full-oracle', 'incremental-oracle', 'fresh-full-oracle', 'retained-old-oracle', 'range-oracle', 'first-window', 'last-window', 'full-consumer-oracle', 'range-consumer-oracle')) {
    $before = @($old.Lines | Where-Object { $_.phase -eq $phase })
    $after = @($new.Lines | Where-Object { $_.phase -eq $phase })
    if ($before.Count -ne $after.Count) { throw "Missing phase: $phase" }
    $required = $phase -in @('full-oracle', 'incremental-oracle', 'fresh-full-oracle', 'retained-old-oracle') -or
        ($phase -in @('first-window', 'last-window') -and $old.Header.scenario -ne 'invalid') -or
        ($phase -eq 'range-oracle' -and $old.Header.scenario -in @('complex', 'mixed')) -or
        ($phase -like '*consumer-oracle' -and $old.Header.scenario -eq 'mixed')
    if ($required -and $before.Count -ne 1) { throw "Required unique oracle missing: $phase" }
    if ($before.Count -eq 1 -and $before[0].digest -ne $after[0].digest) { throw "Full formal digest mismatch: $phase" }
}
foreach ($phase in @('full', 'incremental', 'fresh-full', 'first-window', 'last-window')) {
    $before = @($old.Lines | Where-Object { $_.phase -eq $phase })
    $after = @($new.Lines | Where-Object { $_.phase -eq $phase })
    if ($before.Count -eq 1 -and $after.Count -eq 1) {
        [pscustomobject]@{ Phase = $phase; BaselineMs = $before[0].elapsedMs; CandidateMs = $after[0].elapsedMs;
            BaselineCpuMs = $before[0].cpuMs; CandidateCpuMs = $after[0].cpuMs;
            BaselineAllocationBytes = $before[0].allocatedBytes; CandidateAllocationBytes = $after[0].allocatedBytes;
            BaselineReadBytes = $before[0].io.readBytes; CandidateReadBytes = $after[0].io.readBytes;
            BaselineWriteBytes = $before[0].io.writeBytes; CandidateWriteBytes = $after[0].io.writeBytes }
    }
}
[pscustomobject]@{ Phase = 'job-peak'; BaselinePrivateBytes = $old.Guard.peakTreePrivateBytes;
    CandidatePrivateBytes = $new.Guard.peakTreePrivateBytes; BaselineWorkingSetBytes = $old.Guard.peakTreeWorkingSetBytes;
    CandidateWorkingSetBytes = $new.Guard.peakTreeWorkingSetBytes }
