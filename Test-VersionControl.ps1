[CmdletBinding()]
param(
    [string]$ExpectedVersion,
    [switch]$RequireClean,
    [switch]$RequireTagAtHead
)

$ErrorActionPreference = "Stop"
$repositoryRoot = [System.IO.Path]::GetFullPath($PSScriptRoot)
$versionPropsPath = Join-Path $repositoryRoot "eng\Version.props"
[xml]$versionProps = Get-Content -LiteralPath $versionPropsPath -Raw -Encoding utf8
$propertyGroup = $versionProps.Project.PropertyGroup
$prefix = [string]$propertyGroup.VersionPrefix
$suffix = [string]$propertyGroup.VersionSuffix
$assemblyVersion = [string]$propertyGroup.AssemblyVersion
$fileVersion = [string]$propertyGroup.FileVersion

if ($prefix -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
    throw "VersionPrefix is not a canonical three-part SemVer core: $prefix"
}
if (-not [string]::IsNullOrEmpty($suffix) -and
    $suffix -notmatch '^[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*$') {
    throw "VersionSuffix is not a canonical SemVer prerelease identifier: $suffix"
}

$productVersion = if ([string]::IsNullOrEmpty($suffix)) { $prefix } else { "$prefix-$suffix" }
if (-not [string]::IsNullOrEmpty($ExpectedVersion) -and $productVersion -ne $ExpectedVersion) {
    throw "Product version mismatch: expected=$ExpectedVersion, actual=$productVersion"
}

$parts = $prefix.Split('.')
$expectedAssemblyVersion = "$($parts[0]).0.0.0"
$expectedFileVersion = "$prefix.0"
if ($assemblyVersion -ne $expectedAssemblyVersion) {
    throw "AssemblyVersion must remain major-stable: expected=$expectedAssemblyVersion, actual=$assemblyVersion"
}
if ($fileVersion -ne $expectedFileVersion) {
    throw "FileVersion must identify this product build: expected=$expectedFileVersion, actual=$fileVersion"
}

$productionVersionSources = @(
    "src\midora-desktop\Midora.Desktop\DesktopSessionController.cs",
    "src\midora-desktop\Midora.Desktop\MidiExportServices.cs"
)
foreach ($relativePath in $productionVersionSources) {
    $path = Join-Path $repositoryRoot $relativePath
    if (Select-String -LiteralPath $path -Pattern 'SoftwareVersion\s*=\s*"|0\.1\.0-dev' -Quiet) {
        throw "A production software-version literal was reintroduced in $relativePath."
    }
    if (-not (Select-String -LiteralPath $path -Pattern 'MidoraSoftwareVersion\.InformationalVersion' -Quiet)) {
        throw "The production version consumer does not use MidoraSoftwareVersion: $relativePath"
    }
}

$persistenceContractV1 = Get-Content -LiteralPath (
    Join-Path $repositoryRoot "src\midora-core\Midora.Persistence\PersistenceContractV1.cs") `
    -Raw -Encoding utf8
if ($persistenceContractV1 -notmatch 'public const int FileFormatVersion = 1;' -or
    $persistenceContractV1 -notmatch 'public const int SchemaVersion = 1;') {
    throw "The frozen Project Format 1 contract changed without introducing a new versioned contract."
}

$persistenceContractV2 = Get-Content -LiteralPath (
    Join-Path $repositoryRoot "src\midora-core\Midora.Persistence\PersistenceContractV2.cs") `
    -Raw -Encoding utf8
if ($persistenceContractV2 -notmatch 'public const int FileFormatVersion = 2;' -or
    $persistenceContractV2 -notmatch 'public const int ManifestSchemaVersion = 2;' -or
    $persistenceContractV2 -notmatch 'public const int EventInstrumentSchemaVersion = 2;') {
    throw "The current Project Format 2 contract is not the expected versioned contract."
}
$currentProjectFormatVersion = 2

if ($RequireClean -or $RequireTagAtHead) {
    $status = & git -C $repositoryRoot status --porcelain
    if ($LASTEXITCODE -ne 0) {
        throw "git status failed."
    }
    if ($RequireClean -and $status) {
        throw "The release worktree is not clean."
    }
}

if ($RequireTagAtHead) {
    $expectedTag = "v$productVersion"
    $head = (& git -C $repositoryRoot rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw "git rev-parse HEAD failed."
    }
    $tagCommit = (& git -C $repositoryRoot rev-list -n 1 $expectedTag 2>$null).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrEmpty($tagCommit)) {
        throw "The expected release tag does not exist: $expectedTag"
    }
    if ($tagCommit -ne $head) {
        throw "The expected release tag $expectedTag does not identify HEAD."
    }
}

Write-Host "Midora version contract passed: product=$productVersion, assembly=$assemblyVersion, file=$fileVersion, project-format=$currentProjectFormatVersion."
