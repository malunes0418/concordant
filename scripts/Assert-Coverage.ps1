#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Summarizes Cobertura coverage and optionally enforces a line-coverage floor.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ResultsDirectory,

    [double]$MinimumLinePercent,

    [string]$BaselinePath,

    [string]$ReportPath
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $ResultsDirectory)) {
    throw "Coverage results directory not found: $ResultsDirectory"
}

$coberturaFiles = Get-ChildItem -LiteralPath $ResultsDirectory -Recurse -Filter 'coverage.cobertura.xml'
if ($coberturaFiles.Count -eq 0) {
    throw "No coverage.cobertura.xml files found under $ResultsDirectory"
}

$totalLines = 0
$coveredLines = 0
$perFile = @()

foreach ($file in $coberturaFiles) {
    [xml]$xml = Get-Content -LiteralPath $file.FullName
    $coverage = $xml.coverage
    if (-not $coverage) {
        throw "Invalid Cobertura document: $($file.FullName)"
    }

    $linesValid = [int]$coverage.'lines-valid'
    $linesCovered = [int]$coverage.'lines-covered'
    $lineRate = [double]$coverage.'line-rate'

    # Skip empty collector attachments (e.g. projects with no instrumented lines).
    if ($linesValid -le 0) {
        continue
    }

    $totalLines += $linesValid
    $coveredLines += $linesCovered

    $perFile += [pscustomobject]@{
        Path         = $file.FullName
        LinesValid   = $linesValid
        LinesCovered = $linesCovered
        LinePercent  = [math]::Round($lineRate * 100, 2)
    }
}

if ($perFile.Count -eq 0) {
    throw "No non-empty Cobertura coverage files found under $ResultsDirectory"
}

$aggregatePercent = if ($totalLines -eq 0) { 0.0 } else { [math]::Round(100.0 * $coveredLines / $totalLines, 2) }

Write-Host "Coverage files: $($coberturaFiles.Count)"
foreach ($row in $perFile) {
    Write-Host ("  {0:N2}%  ({1}/{2})  {3}" -f $row.LinePercent, $row.LinesCovered, $row.LinesValid, $row.Path)
}

Write-Host ("Aggregate line coverage: {0:N2}% ({1}/{2})" -f $aggregatePercent, $coveredLines, $totalLines)

if ($BaselinePath -and (Test-Path -LiteralPath $BaselinePath)) {
    $baseline = Get-Content -LiteralPath $BaselinePath -Raw | ConvertFrom-Json
    if ($null -eq $MinimumLinePercent -or $MinimumLinePercent -le 0) {
        $MinimumLinePercent = [double]$baseline.lineCoverageMinimum
    }

    Write-Host "Baseline file: $BaselinePath (measured=$($baseline.measuredLineCoveragePercent)% minimum=$($baseline.lineCoverageMinimum)%)"
}

if ($PSBoundParameters.ContainsKey('MinimumLinePercent') -or ($BaselinePath -and (Test-Path -LiteralPath $BaselinePath))) {
    if ($aggregatePercent -lt $MinimumLinePercent) {
        throw "Coverage $aggregatePercent% is below required minimum $MinimumLinePercent%."
    }

    Write-Host "OK  coverage meets minimum $MinimumLinePercent%"
}

if ($ReportPath) {
    $dir = Split-Path -Parent $ReportPath
    if ($dir -and -not (Test-Path -LiteralPath $dir)) {
        New-Item -ItemType Directory -Path $dir | Out-Null
    }

    $summary = [ordered]@{
        measuredUtc                = [DateTime]::UtcNow.ToString('o')
        aggregateLineCoveragePercent = $aggregatePercent
        linesCovered               = $coveredLines
        linesValid                 = $totalLines
        files                      = @($perFile | ForEach-Object {
                [ordered]@{
                    path         = $_.Path
                    linePercent  = $_.LinePercent
                    linesCovered = $_.LinesCovered
                    linesValid   = $_.LinesValid
                }
            })
    }

    ($summary | ConvertTo-Json -Depth 6) | Set-Content -LiteralPath $ReportPath -Encoding utf8
    Write-Host "Wrote coverage summary: $ReportPath"
}

# Emit a machine-friendly value for workflow step outputs.
Write-Output $aggregatePercent
