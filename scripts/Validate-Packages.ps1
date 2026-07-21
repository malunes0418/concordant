#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Validates packed Concordant .nupkg / .snupkg files and optional release-tag alignment.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackageDirectory,

    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,

    [string]$ExpectedVersion,

    [string]$ReleaseTag
)

$ErrorActionPreference = 'Stop'

function Get-ExpectedVersion {
    param([string]$Root)
    & (Join-Path $PSScriptRoot 'Get-PackageVersion.ps1') -RepoRoot $Root
}

function Expand-Nupkg {
    param(
        [string]$NupkgPath,
        [string]$Destination
    )

    if (Test-Path -LiteralPath $Destination) {
        Remove-Item -LiteralPath $Destination -Recurse -Force
    }

    New-Item -ItemType Directory -Path $Destination | Out-Null
    # .nupkg is a zip archive
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::ExtractToDirectory($NupkgPath, $Destination)
}

function Assert-PathExists {
    param(
        [string]$Path,
        [string]$Label
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Missing $Label`: $Path"
    }
}

function Get-NuspecMetadata {
    param([string]$ExtractRoot)

    $nuspec = Get-ChildItem -LiteralPath $ExtractRoot -Filter '*.nuspec' | Select-Object -First 1
    if (-not $nuspec) {
        throw "No .nuspec found under $ExtractRoot"
    }

    [xml]$xml = Get-Content -LiteralPath $nuspec.FullName
    $md = $xml.package.metadata
    if (-not $md) {
        throw "Invalid nuspec metadata in $($nuspec.FullName)"
    }

    [pscustomobject]@{
        Id      = [string]$md.id
        Version = [string]$md.version
        Path    = $nuspec.FullName
    }
}

if (-not (Test-Path -LiteralPath $PackageDirectory)) {
    throw "Package directory not found: $PackageDirectory"
}

if ([string]::IsNullOrWhiteSpace($ExpectedVersion)) {
    $ExpectedVersion = Get-ExpectedVersion -Root $RepoRoot
}

Write-Host "Expected package version: $ExpectedVersion"

$requiredIds = @(
    'Concordant.Core',
    'Concordant.Persistence.Abstractions'
)

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("concordant-pkg-validate-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempRoot | Out-Null

try {
    foreach ($id in $requiredIds) {
        $nupkgName = "$id.$ExpectedVersion.nupkg"
        $snupkgName = "$id.$ExpectedVersion.snupkg"
        $nupkgPath = Join-Path $PackageDirectory $nupkgName
        $snupkgPath = Join-Path $PackageDirectory $snupkgName

        Assert-PathExists -Path $nupkgPath -Label 'nupkg'
        Assert-PathExists -Path $snupkgPath -Label 'snupkg'

        $extractDir = Join-Path $tempRoot $id
        Expand-Nupkg -NupkgPath $nupkgPath -Destination $extractDir

        $meta = Get-NuspecMetadata -ExtractRoot $extractDir
        if ($meta.Id -ne $id) {
            throw "Package id mismatch for $nupkgName (nuspec id=$($meta.Id))"
        }

        if ($meta.Version -ne $ExpectedVersion) {
            throw "Package version mismatch for $nupkgName (nuspec=$($meta.Version), expected=$ExpectedVersion)"
        }

        Assert-PathExists -Path (Join-Path $extractDir 'README.md') -Label "$id README.md"
        Assert-PathExists -Path (Join-Path $extractDir "lib/net8.0/$id.dll") -Label "$id net8.0 assembly"
        Assert-PathExists -Path (Join-Path $extractDir "lib/net10.0/$id.dll") -Label "$id net10.0 assembly"

        # Symbol package must contain PDB entries for both TFMs.
        $symbolDir = Join-Path $tempRoot "$id-symbols"
        Expand-Nupkg -NupkgPath $snupkgPath -Destination $symbolDir
        $pdbs = Get-ChildItem -LiteralPath $symbolDir -Recurse -Filter '*.pdb'
        if ($pdbs.Count -lt 1) {
            throw "Symbol package $snupkgName contains no .pdb files"
        }

        $hasNet8Pdb = $pdbs | Where-Object { $_.FullName -match '[\\/]lib[\\/]net8\.0[\\/]' }
        $hasNet10Pdb = $pdbs | Where-Object { $_.FullName -match '[\\/]lib[\\/]net10\.0[\\/]' }
        if (-not $hasNet8Pdb) {
            throw "Symbol package $snupkgName missing lib/net8.0 PDB"
        }

        if (-not $hasNet10Pdb) {
            throw "Symbol package $snupkgName missing lib/net10.0 PDB"
        }

        Write-Host "OK  $nupkgName (+ snupkg, dual TFM, README, symbols)"
    }

    # Reject unexpected Concordant packages with wrong version.
    $allConcordant = Get-ChildItem -LiteralPath $PackageDirectory -Filter 'Concordant.*.nupkg'
    foreach ($pkg in $allConcordant) {
        if ($pkg.Name -notmatch '^Concordant\.(Core|Persistence\.Abstractions)\.') {
            throw "Unexpected Concordant package present: $($pkg.Name)"
        }

        if ($pkg.Name -notlike "*.$ExpectedVersion.nupkg") {
            throw "Found Concordant package with unexpected version: $($pkg.Name) (expected $ExpectedVersion)"
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($ReleaseTag)) {
        $normalizedTag = $ReleaseTag.Trim()
        if ($normalizedTag.StartsWith('v', [System.StringComparison]::OrdinalIgnoreCase)) {
            $normalizedTag = $normalizedTag.Substring(1)
        }

        if ($normalizedTag -ne $ExpectedVersion) {
            throw "Release tag '$ReleaseTag' does not match package version '$ExpectedVersion' (from Directory.Build.props / packages)."
        }

        Write-Host "OK  release tag '$ReleaseTag' aligns with package version '$ExpectedVersion'"
    }
    else {
        Write-Host 'Skip release-tag alignment (no -ReleaseTag provided).'
    }

    Write-Host 'Package validation passed.'
}
finally {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
    }
}
