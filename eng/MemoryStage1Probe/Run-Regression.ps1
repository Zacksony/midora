[CmdletBinding()]
param([string]$ResultsDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if ([string]::IsNullOrWhiteSpace($ResultsDirectory)) {
    $ResultsDirectory = Join-Path $repositoryRoot ('.tmp/memory-stage1/regression-' + [Guid]::NewGuid().ToString('N'))
}
$results = [IO.Path]::GetFullPath($ResultsDirectory)
if (Test-Path -LiteralPath $results) { throw 'Choose a new regression result directory.' }
New-Item -ItemType Directory -Path $results | Out-Null
$projects = @(
    'src/midora-core/Midora.Application.Tests/Midora.Application.Tests.csproj',
    'src/midora-core/Midora.Compiler.Tests/Midora.Compiler.Tests.csproj',
    'src/midora-core/Midora.Persistence.Tests/Midora.Persistence.Tests.csproj',
    'src/midora-core/Midora.MidiExport.Tests/Midora.MidiExport.Tests.csproj',
    'src/midora-core/Midora.Playback.Tests/Midora.Playback.Tests.csproj',
    'src/midora-core/Midora.AudioRender.Tests/Midora.AudioRender.Tests.csproj',
    'src/midora-desktop/Midora.Desktop.Presentation.Tests/Midora.Desktop.Presentation.Tests.csproj',
    'src/midora-desktop/Midora.Desktop.Tests/Midora.Desktop.Tests.csproj'
)
foreach ($project in $projects) {
    $name = [IO.Path]::GetFileNameWithoutExtension($project)
    & dotnet test (Join-Path $repositoryRoot $project) -c Release --no-restore --nologo `
        --logger "trx;LogFileName=$name.trx" --results-directory $results
    if ($LASTEXITCODE -ne 0) { throw "Regression failed: $name. Results: $results" }
}
Write-Output "Regression TRX: $results"
