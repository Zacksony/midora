[CmdletBinding()]
param([Parameter(Mandatory)][string]$ResultsDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$culture = [Globalization.CultureInfo]::InvariantCulture

function Get-Median([double[]]$Values) {
    $ordered = @($Values | Sort-Object)
    $middle = [int][Math]::Floor($ordered.Count / 2)
    if ($ordered.Count % 2) { return $ordered[$middle] }
    return ($ordered[$middle - 1] + $ordered[$middle]) / 2
}

$runs = @(foreach ($file in Get-ChildItem -LiteralPath $ResultsDirectory -Filter '*.log') {
    $name = [IO.Path]::GetFileNameWithoutExtension($file.Name)
    if ($name -notmatch '^(?<size>1m|18m)-(?<mode>roundtrip|edited|save)-(?<variant>before|after)-(?<repeat>\d+)$') { continue }
    $identity = @{ Size = $Matches.size; Mode = $Matches.mode; Variant = $Matches.variant; Repeat = [int]$Matches.repeat }
    $lines = @(Get-Content -LiteralPath $file.FullName)
    $phases = @($lines | Where-Object { $_ -match '^\d+\.\d+,' } | ConvertFrom-Csv -Header `
        seconds,phase,wsMiB,privateMiB,peakWsMiB,managedMiB,heapMiB,fragmentedMiB,committedMiB,allocatedMiB,gen0,gen1,gen2)
    if (-not ($phases | Where-Object phase -eq exit) -or
        -not ($lines | Where-Object { $_ -match '"weakProjectAlive":\[false(,false)*\]' })) {
        throw "Incomplete, failed, or retained-Project measurement: $name"
    }
    $samples = @(Import-Csv -LiteralPath (Join-Path $ResultsDirectory "$name/memory.csv"))
    $metrics = [ordered]@{}
    foreach ($operation in @('import', 'save', 'copy', 'reopen')) {
        $start = @($phases | Where-Object phase -eq "$operation-start")
        $end = @($phases | Where-Object phase -eq "$operation-return")
        if ($start.Count -eq 1 -and $end.Count -eq 1) {
            $metrics[$operation + 'Seconds'] = [double]::Parse($end[0].seconds, $culture) - [double]::Parse($start[0].seconds, $culture)
        }
    }
    foreach ($property in @('peakWsMiB', 'privateMiB', 'managedMiB', 'committedMiB', 'allocatedMiB')) {
        $metrics[$property] = ($samples | ForEach-Object { [double]::Parse($_.$property, $culture) } | Measure-Object -Maximum).Maximum
    }
    $exit = $phases | Where-Object phase -eq exit | Select-Object -Last 1
    $released = $phases | Where-Object phase -eq idle-after-GC | Select-Object -Last 1
    $metrics['totalSeconds'] = [double]::Parse($exit.seconds, $culture)
    $metrics['releasedManagedMiB'] = [double]::Parse($released.managedMiB, $culture)
    $identity['Metrics'] = $metrics
    $identity['Evidence'] = $file.FullName
    [pscustomobject]$identity
})
if ($runs.Count -eq 0) { throw 'No named successful measurements were found.' }

$summary = @(foreach ($group in $runs | Group-Object Size,Mode,Variant) {
    foreach ($metric in $group.Group[0].Metrics.Keys) {
        $values = @($group.Group | ForEach-Object { $_.Metrics[$metric] })
        [pscustomobject]@{
            Size = $group.Group[0].Size; Mode = $group.Group[0].Mode; Variant = $group.Group[0].Variant
            Metric = $metric; N = $values.Count; Median = Get-Median $values
            Min = ($values | Measure-Object -Minimum).Minimum; Max = ($values | Measure-Object -Maximum).Maximum
        }
    }
})
[pscustomobject]@{ Runs = $runs; Summary = $summary }
