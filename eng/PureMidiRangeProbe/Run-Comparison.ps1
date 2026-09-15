[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$GuardAssembly,
    [Parameter(Mandatory)][string]$BaselineReferences,
    [Parameter(Mandatory)][string]$CandidateReferences,
    [Parameter(Mandatory)][string]$MidiPath,
    [Parameter(Mandatory)][string]$ResultsDirectory,
    [ValidateRange(1, 10)][int]$Repetitions = 3
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$results = [IO.Path]::GetFullPath($ResultsDirectory)
$temporaryRoot = Join-Path $repositoryRoot '.tmp'
if (-not $results.StartsWith($temporaryRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Comparison artifacts must use a new directory under the repository .tmp folder.'
}
if (Test-Path -LiteralPath $results) { throw 'Choose a new comparison directory; old evidence is never overwritten.' }
$guard = (Resolve-Path -LiteralPath $GuardAssembly).Path
$source = (Resolve-Path -LiteralPath $MidiPath).Path
$dotnet = (Get-Command dotnet -CommandType Application).Source
$variants = @(
    @{ Name = 'before'; References = (Resolve-Path -LiteralPath $BaselineReferences).Path },
    @{ Name = 'after'; References = (Resolve-Path -LiteralPath $CandidateReferences).Path }
)
New-Item -ItemType Directory -Path $results | Out-Null
foreach ($variant in $variants) {
    $binary = Join-Path $results ($variant.Name + '-probe')
    & $dotnet build (Join-Path $PSScriptRoot 'PureMidiRangeProbe.csproj') -c Release --no-restore --nologo `
        -m:1 -nodeReuse:false -p:UseSharedCompilation=false "-p:MemoryProbeReferenceDirectory=$($variant.References)" -o $binary
    if ($LASTEXITCODE -ne 0) { throw "Probe build failed: $($variant.Name)." }
}
for ($trial = 1; $trial -le $Repetitions; $trial++) {
    foreach ($variant in $variants) {
        $name = $variant.Name + '-' + $trial
        $output = Join-Path $results $name
        $guardOutput = Join-Path $results ($name + '-guard')
        $assembly = Join-Path $results ($variant.Name + '-probe/PureMidiRangeProbe.dll')
        $arguments = @($assembly, $source, $output)
        if ($variant.Name -eq 'after') { $arguments += 'verify' }
        & $dotnet $guard guard $guardOutput 8192 2048 $dotnet @arguments
        if ($LASTEXITCODE -ne 0) { throw "Comparison run failed or hit the safety guard: $name. No further run was started." }
    }
}
Write-Output "Completed serial comparison: $results"
