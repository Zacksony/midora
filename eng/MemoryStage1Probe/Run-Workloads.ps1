[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BeforeProbeDirectory,
    [Parameter(Mandatory)][string]$AfterProbeDirectory,
    [Parameter(Mandatory)][string]$OneMillionMidi,
    [Parameter(Mandatory)][string]$EighteenMillionMidi,
    [Parameter(Mandatory)][string]$ResultsDirectory,
    [ValidateSet('roundtrip', 'edited', 'save')][string[]]$Modes = @('roundtrip', 'edited', 'save'),
    [ValidateRange(1, 5)][int]$Repeats = 3,
    [ValidateRange(30, 3600)][int]$TimeoutSeconds = 900
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$results = [IO.Path]::GetFullPath($ResultsDirectory)
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$temporaryRoot = Join-Path $repository '.tmp'
if (-not $results.StartsWith($temporaryRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Measurement results must use a new subdirectory of repository .tmp.'
}
if (Test-Path -LiteralPath $results) { throw 'Choose a new measurement result directory.' }
$dotnet = (Get-Command dotnet -CommandType Application | Select-Object -First 1).Source
$probes = @{
    before = (Resolve-Path -LiteralPath (Join-Path $BeforeProbeDirectory 'Midora.Persistence.Tests.dll')).Path
    after = (Resolve-Path -LiteralPath (Join-Path $AfterProbeDirectory 'Midora.Persistence.Tests.dll')).Path
}
$samples = [ordered]@{
    '1m' = (Resolve-Path -LiteralPath $OneMillionMidi).Path
    '18m' = (Resolve-Path -LiteralPath $EighteenMillionMidi).Path
}
New-Item -ItemType Directory -Path $results | Out-Null
foreach ($sample in $samples.GetEnumerator()) {
    foreach ($mode in $Modes) {
        for ($repeat = 1; $repeat -le $Repeats; $repeat++) {
            foreach ($variant in @('before', 'after')) {
                $name = "$($sample.Key)-$mode-$variant-$repeat"
                $output = Join-Path $results $name
                $logPath = $output + '.log'
                # Reserve headroom for import pages, detached edits, staged packs,
                # both saved packages and transaction copies. This is a probe
                # safety check, not a new product storage or project-size limit.
                $requiredFree = 2GB + 12 * (Get-Item -LiteralPath $sample.Value).Length
                $drive = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($results))
                if ($drive.AvailableFreeSpace -lt $requiredFree) { throw "Insufficient probe disk headroom for $name." }
                $info = [Diagnostics.ProcessStartInfo]::new($dotnet)
                $info.UseShellExecute = $false
                $info.CreateNoWindow = $true
                $info.RedirectStandardOutput = $true
                $info.RedirectStandardError = $true
                foreach ($argument in @($probes[$variant], $mode, $sample.Value, $output)) { $info.ArgumentList.Add($argument) }
                $process = [Diagnostics.Process]::new()
                $process.StartInfo = $info
                $log = [IO.StreamWriter]::new($logPath, $false, [Text.UTF8Encoding]::new($false))
                $started = $false
                try {
                    $started = $process.Start()
                    if (-not $started) { throw "Could not start probe $name." }
                    # Read both pipes concurrently to avoid deadlock. Logs are
                    # small; note payloads and memory samples are never in them.
                    $stdout = $process.StandardOutput.ReadToEndAsync()
                    $stderr = $process.StandardError.ReadToEndAsync()
                    $finished = $process.WaitForExit($TimeoutSeconds * 1000)
                    if (-not $finished) {
                        # Only this owned test process tree may be terminated.
                        # Preserve partial output for diagnosis, never reuse it.
                        if (-not $process.HasExited) { $process.Kill($true) }
                        $process.WaitForExit()
                    }
                    $log.Write($stdout.GetAwaiter().GetResult())
                    $log.Write($stderr.GetAwaiter().GetResult())
                    if (-not $finished) { throw "Probe timeout: $name. Partial evidence: $output" }
                    if ($process.ExitCode -ne 0) { throw "Probe failed: $name ($($process.ExitCode)). Evidence: $output" }
                }
                finally {
                    try {
                        if ($started -and -not $process.HasExited) {
                            $process.Kill($true)
                            [void]$process.WaitForExit(5000)
                        }
                    }
                    finally { $log.Dispose(); $process.Dispose() }
                }
                Write-Output "Completed $name"
            }
        }
    }
}
Write-Output "Measurements: $results"
