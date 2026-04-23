<#
.SYNOPSIS
Enforces the repo's minimum coverage threshold.

.DESCRIPTION
Parses every *.cobertura.xml under src/**/TestResults/, computes the
aggregate line-coverage rate across all test assemblies, and exits
non-zero if the rate is below the -Threshold (default 90).

Invoked by docs/best-practices.md workflow:
    dotnet test --solution Weave.slnx -c Release -- --coverage --coverage-output-format cobertura
    pwsh ./scripts/check-coverage.ps1

.PARAMETER Threshold
Minimum acceptable overall line coverage, 0-100. Default 90.

.PARAMETER SearchRoot
Root folder to scan for cobertura files. Default 'src'.
#>

[CmdletBinding()]
param (
    [double]$Threshold = 90,
    [string]$SearchRoot = 'src'
)

$ErrorActionPreference = 'Stop'

$reports = Get-ChildItem -Path $SearchRoot -Recurse -Filter '*.cobertura.xml' -ErrorAction Ignore
if (-not $reports) {
    Write-Host "No cobertura.xml files found under '$SearchRoot'. Did the test run collect coverage?" -ForegroundColor Yellow
    Write-Host "  dotnet test --solution Weave.slnx -c Release -- --coverage --coverage-output-format cobertura"
    exit 2
}

# Aggregate at the <package> level.
#
# Each cobertura file lists EVERY package its test run touched —
# including referenced production packages it doesn't own. If Weave.Shared
# is referenced by Weave.Security.Tests, the Security.Tests cobertura
# has a Weave.Shared package entry at whatever partial coverage Security's
# tests exercised of it. Summing those across all cobertura files
# multiplies the denominator and reports a misleading low number.
#
# Rule: each production package is counted ONCE — from the cobertura
# whose owning test project matches (Weave.X.Tests -> Weave.X).
$perPackage = @{}

foreach ($report in $reports) {
    [xml]$xml = Get-Content $report.FullName

    # Derive the owning test project from the cobertura's path.
    # ...\Weave.X.Tests\bin\Release\net10.0\TestResults\*.cobertura.xml
    $tfmDir = Split-Path (Split-Path $report.FullName -Parent) -Parent
    $configDir = Split-Path $tfmDir -Parent
    $binDir = Split-Path $configDir -Parent
    $testProjectDir = Split-Path $binDir -Parent
    $owningTestProject = Split-Path $testProjectDir -Leaf
    # Weave.Security.Tests -> Weave.Security
    $ownedPackage = if ($owningTestProject.EndsWith('.Tests')) {
        $owningTestProject.Substring(0, $owningTestProject.Length - 6)
    } else { $null }

    foreach ($pkg in $xml.coverage.packages.package) {
        $name = [string]$pkg.name
        if ([string]::IsNullOrEmpty($name)) { continue }

        # Test assemblies are not counted.
        if ($name -like '*.Tests' -or $name -like '*Tests*') { continue }

        # Only count a package from its OWNING test project's cobertura.
        # Skip references that happened to be exercised as a side-effect
        # (e.g. Weave.Shared appearing in Weave.Security.Tests' report).
        if ($ownedPackage -ne $null -and $name -ne $ownedPackage) { continue }

        $covered = 0
        $valid = 0
        foreach ($cls in $pkg.classes.class) {
            $filename = [string]$cls.filename
            if ($filename -match '\\obj\\' -or
                $filename -match '\.g\.cs$' -or
                $filename -match '\\Models\\' -or
                $filename -like '*Surrogate*' -or
                $filename -like '*Contracts.cs') {
                continue
            }

            foreach ($line in $cls.lines.line) {
                $valid++
                if ([int]$line.hits -gt 0) { $covered++ }
            }
        }

        if (-not $perPackage.ContainsKey($name)) {
            $perPackage[$name] = [pscustomobject]@{ Covered = 0; Valid = 0 }
        }
        # One cobertura per owning test project, so this is a simple assignment.
        $perPackage[$name].Covered = $covered
        $perPackage[$name].Valid = $valid
    }
}

$totalLinesCovered = 0
$totalLinesValid = 0
$perAssembly = foreach ($name in ($perPackage.Keys | Sort-Object)) {
    $p = $perPackage[$name]
    $rate = if ($p.Valid -gt 0) { ($p.Covered / $p.Valid) * 100 } else { 0 }
    $totalLinesCovered += $p.Covered
    $totalLinesValid += $p.Valid
    [pscustomobject]@{
        Assembly = $name
        Covered  = $p.Covered
        Valid    = $p.Valid
        LineRate = [math]::Round($rate, 1)
    }
}

$overallRate = if ($totalLinesValid -gt 0) { ($totalLinesCovered / $totalLinesValid) * 100 } else { 0 }

Write-Host ""
Write-Host "Coverage per assembly:" -ForegroundColor Cyan
$perAssembly | Sort-Object LineRate | Format-Table -AutoSize

Write-Host ('Overall: {0:0.0}%  ({1:N0} / {2:N0} lines)  threshold {3:0.0}%' -f `
    $overallRate, $totalLinesCovered, $totalLinesValid, $Threshold) -ForegroundColor Cyan

if ($overallRate -lt $Threshold) {
    Write-Host ''
    Write-Host ('COVERAGE BELOW THRESHOLD: {0:0.0}% < {1:0.0}%' -f $overallRate, $Threshold) -ForegroundColor Red
    Write-Host 'Add tests to bring the overall rate up to or above the threshold.' -ForegroundColor Red
    Write-Host 'Quality rules from docs/best-practices.md still apply - coverage raised by assertion-free tests is rejected in review.'
    exit 1
}

Write-Host "Coverage meets threshold." -ForegroundColor Green
exit 0
