[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$GuardAssembly,
    [Parameter(Mandatory)][string]$ResultsDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$results = [IO.Path]::GetFullPath($ResultsDirectory)
if (-not $results.StartsWith($repositoryRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Use a new result directory inside this repository.'
}
if (Test-Path -LiteralPath $results) { throw 'Choose a new regression result directory.' }
$guard = (Resolve-Path -LiteralPath $GuardAssembly).Path
$dotnet = (Get-Command dotnet -CommandType Application).Source
New-Item -ItemType Directory -Path $results | Out-Null
$projects = @(
    'src/midora-common/Midora.Common.Tests/Midora.Common.Tests.csproj',
    'src/midora-midi/Midora.Midi.Tests/Midora.Midi.Tests.csproj',
    'src/midora-core/Midora.Application.Tests/Midora.Application.Tests.csproj',
    'src/midora-core/Midora.Compiler.Tests/Midora.Compiler.Tests.csproj',
    'src/midora-core/Midora.Persistence.Tests/Midora.Persistence.Tests.csproj',
    'src/midora-core/Midora.MidiExport.Tests/Midora.MidiExport.Tests.csproj',
    'src/midora-core/Midora.Playback.Tests/Midora.Playback.Tests.csproj',
    'src/midora-core/Midora.AudioRender.Tests/Midora.AudioRender.Tests.csproj',
    'src/midora-desktop/Midora.Desktop.Presentation.Tests/Midora.Desktop.Presentation.Tests.csproj',
    'src/midora-desktop/Midora.Desktop.Tests/Midora.Desktop.Tests.csproj',
    'src/midora-audio/Midora.Audio.Bass.Tests/Midora.Audio.Bass.Tests.csproj'
)
$runs = [Collections.Generic.List[object]]::new()
foreach ($project in $projects) {
    $name = [IO.Path]::GetFileNameWithoutExtension($project)
    $output = Join-Path $results ($name + '/bin/')
    $guardDirectory = Join-Path $results ($name + '/guard')
    $trx = Join-Path $results ($name + '.trx')
    $arguments = @('test', (Join-Path $repositoryRoot $project), '-c', 'Release', '--no-restore', '--nologo',
        '-m:1', '-nodeReuse:false', '-p:UseSharedCompilation=false',
        "-p:OutDir=$output", '--logger', "trx;LogFileName=$name.trx", '--results-directory', $results)
    if ($name -eq 'Midora.Audio.Bass.Tests') {
        # Managed gates only. Do not invoke native fixtures with legacy path fallback.
        $types = @('AudioFrameRingBufferTests', 'SharedAudioFrameRingBufferTests',
            'SharedAudioWorkerControlTests', 'StereoLookAheadLimiterTests',
            'UnitPcmCacheIoBridgeTests', 'WaveFileOutputTests', 'AudioPcmCachePayloadTests',
            'MidiRenderPlanFileTests', 'MidiRenderPlanTests', 'MidiRenderEventStreamProtocolTests')
        $arguments += @('--filter', (($types | ForEach-Object { "FullyQualifiedName~.$_" }) -join '|'))
    }
    & $dotnet $guard guard $guardDirectory 8192 2048 $dotnet @arguments
    $exitCode = $LASTEXITCODE
    $guardResultPath = Join-Path $guardDirectory 'guard-result.json'
    if (-not (Test-Path -LiteralPath $guardResultPath)) {
        throw "Guard did not complete: $name. Do not start another process."
    }
    $guardResult = Get-Content -LiteralPath $guardResultPath -Raw | ConvertFrom-Json
    if ($null -ne $guardResult.stopReason -or $exitCode -in @(124, 125)) {
        throw "Safety guard stopped $name; remaining suites were not started."
    }
    if (-not (Test-Path -LiteralPath $trx)) {
        throw "Build/test discovery did not produce TRX for $name; inspect logs before continuing."
    }
    [xml]$document = Get-Content -LiteralPath $trx -Raw
    $counters = $document.TestRun.ResultSummary.Counters
    if ([int]$counters.total -le 0) { throw "No tests discovered for $name." }
    $runs.Add([ordered]@{
        project = $name; exitCode = $exitCode
        total = [int]$counters.total; passed = [int]$counters.passed; failed = [int]$counters.failed
        notExecuted = [int]$counters.notExecuted
        peakTreePrivateBytes = $guardResult.peakTreePrivateBytes
        peakTreeWorkingSetBytes = $guardResult.peakTreeWorkingSetBytes
    })
    # A known assertion failure must not hide the independent suites. Preserve
    # every nonzero result and return failure after all safe suites have run.
    $runs | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $results 'regression-summary.json') -Encoding utf8
}
if (@($runs | Where-Object { $_.exitCode -ne 0 -or $_.failed -ne 0 -or $_.notExecuted -ne 0 }).Count -ne 0) {
    throw "Regression has failures or unexecuted tests. No failures were suppressed: $results"
}
Write-Output "Guarded regression completed: $results"
