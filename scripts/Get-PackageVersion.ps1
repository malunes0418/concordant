#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Reads the NuGet package version from Directory.Build.props.
#>
[CmdletBinding()]
param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
)

$ErrorActionPreference = 'Stop'

$propsPath = Join-Path $RepoRoot 'Directory.Build.props'
if (-not (Test-Path -LiteralPath $propsPath)) {
    throw "Directory.Build.props not found at $propsPath"
}

[xml]$props = Get-Content -LiteralPath $propsPath
# SDK-style Directory.Build.props typically has no xmlns.
$prefixNode = $props.SelectSingleNode('//VersionPrefix')
$suffixNode = $props.SelectSingleNode('//VersionSuffix')
if (-not $prefixNode) {
    throw 'VersionPrefix not found in Directory.Build.props'
}

$prefix = $prefixNode.InnerText.Trim()
$suffix = if ($suffixNode) { $suffixNode.InnerText.Trim() } else { '' }
$version = if ([string]::IsNullOrWhiteSpace($suffix)) { $prefix } else { "$prefix-$suffix" }

Write-Output $version
