[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SourceDirectory,
    [Parameter(Mandatory)][string]$DestinationDirectory,
    [Parameter(Mandatory)][string]$Label
)
$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $SourceDirectory).Path
$destination = [IO.Path]::GetFullPath($DestinationDirectory)
if (Test-Path -LiteralPath $destination) { throw 'Snapshot destination must not already exist.' }
New-Item -ItemType Directory -Path $destination | Out-Null
$files = Get-ChildItem -LiteralPath $source -File | Where-Object {
    $_.Name -like '*.dll' -or $_.Name -like '*.deps.json' -or $_.Name -like '*.runtimeconfig.json'
}
$manifest = foreach ($file in $files) {
    Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $destination $file.Name)
    $sourceHash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    $copiedHash = (Get-FileHash -LiteralPath (Join-Path $destination $file.Name) -Algorithm SHA256).Hash
    if ($sourceHash -ne $copiedHash) { throw "Source changed while freezing $($file.Name). The incomplete snapshot is not usable." }
    [ordered]@{ name = $file.Name; bytes = $file.Length; sha256 = $copiedHash }
}
[ordered]@{ label = $Label; source = $source; capturedUtc = [DateTime]::UtcNow.ToString('O'); files = @($manifest) } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $destination 'binary-snapshot.json') -Encoding utf8
Write-Output "Frozen $($files.Count) files to $destination"
