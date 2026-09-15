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
foreach ($project in $projects) {
    $name = [IO.Path]::GetFileNameWithoutExtension($project)
    $output = Join-Path $results ($name + '/bin/')
    $arguments = @('test', (Join-Path $repositoryRoot $project), '-c', 'Release', '--no-restore', '--nologo',
        '-m:1', '-nodeReuse:false', '-p:UseSharedCompilation=false',
        "-p:OutDir=$output", '--logger', "trx;LogFileName=$name.trx", '--results-directory', $results)
    if ($name -eq 'Midora.Audio.Bass.Tests') {
        # Managed audio gates: no physical device, SoundFont, Worker publish or dist mutation.
        $types = @('AudioFrameRingBufferTests', 'SharedAudioFrameRingBufferTests',
            'SharedAudioWorkerControlTests', 'StereoLookAheadLimiterTests',
            'UnitPcmCacheIoBridgeTests', 'WaveFileOutputTests', 'AudioPcmCachePayloadTests',
            'MidiRenderPlanFileTests', 'MidiRenderPlanTests', 'MidiRenderEventStreamProtocolTests')
        $arguments += @('--filter', (($types | ForEach-Object { "FullyQualifiedName~.$_" }) -join '|'))
    }
    & $dotnet $guard guard (Join-Path $results ($name + '/guard')) 8192 2048 $dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "Guarded regression failed: $name. Results: $results" }
}
Write-Output "Guarded regression TRX: $results"
