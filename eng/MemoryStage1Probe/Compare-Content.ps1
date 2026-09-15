[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Before,
    [Parameter(Mandatory)][string]$After
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression

function Get-PackageContentHashes([string]$Path) {
    $resolved = (Resolve-Path -LiteralPath $Path).Path
    $archive = [System.IO.Compression.ZipFile]::OpenRead($resolved)
    try {
        $hashes = [System.Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
        foreach ($entry in $archive.Entries) {
            if ($entry.FullName -eq 'manifest.json') { continue }
            $stream = $entry.Open()
            try {
                if ($entry.FullName -eq 'metadata.json') {
                    $reader = [IO.StreamReader]::new($stream)
                    try { $metadata = $reader.ReadToEnd() | ConvertFrom-Json -AsHashtable }
                    finally { $reader.Dispose() }
                    foreach ($field in @('createdAtUtc', 'modifiedAtUtc', 'totalEditingTimeMilliseconds')) { [void]$metadata.Remove($field) }
                    $bytes = [Text.Encoding]::UTF8.GetBytes(($metadata | ConvertTo-Json -Depth 16 -Compress))
                    $hashes.Add($entry.FullName, [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)))
                }
                else {
                    $hashes.Add($entry.FullName, [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)))
                }
            }
            finally { $stream.Dispose() }
        }
        return ,$hashes
    }
    finally { $archive.Dispose() }
}

$beforeHashes = Get-PackageContentHashes $Before
$afterHashes = Get-PackageContentHashes $After
if ($beforeHashes.Count -ne $afterHashes.Count) { throw 'Package entry counts differ.' }
foreach ($entry in $beforeHashes.GetEnumerator()) {
    if (-not $afterHashes.ContainsKey($entry.Key) -or $afterHashes[$entry.Key] -ne $entry.Value) {
        throw "Formal content differs: $($entry.Key)"
    }
}
[pscustomobject]@{
    Equal = $true; ComparedEntries = $beforeHashes.Count
    Ignored = @('metadata.createdAtUtc', 'metadata.modifiedAtUtc', 'metadata.totalEditingTimeMilliseconds', 'manifest.json')
}
