#!/usr/bin/env pwsh
# Validate hexagonal architecture layer boundaries for apps/api.
# Checks that the inner rings stay free of infrastructure:
#   - Huishoudplanner.Domain references no package except OneOf and no other project of ours,
#   - Huishoudplanner.Application references only Domain and no infrastructure package,
#   - no unfiltered `catch (Exception)` under apps/api/src (a `when (...)` filter is the rule),
#   - no DateTime.Now / DateTime.UtcNow in Domain or Application (use TimeProvider),
#   - no MediatR anywhere.
#
# HEURISTIC: a fast, regex-based sanity check, not a substitute for the compiler or for
# Huishoudplanner.Architecture.Tests (ArchUnitNET), which is the authoritative guard. It greps
# .csproj/.cs text and can miss aliased violations or flag text inside a string or comment.
# It is advisory only and ALWAYS exits 0, so it never fails a build on its own.
#
# Usage:
#   ./check-layer-boundaries.ps1
#   ./check-layer-boundaries.ps1 -ApiRoot apps/api

param(
    [string]$ApiRoot = "apps/api"
)

$ErrorActionPreference = "Stop"

# This script lives at .agents/skills/hexagonal-arch-dotnet/scripts/, so the repository root is
# four levels up.
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "../../../..")
$apiPath = Join-Path $repoRoot $ApiRoot
$sourcePath = Join-Path $apiPath "src"
$violations = @()

if (-not (Test-Path $sourcePath)) {
    Write-Host "Source directory not found: $sourcePath" -ForegroundColor Yellow
    Write-Host "apps/api does not exist yet; skipping boundary check" -ForegroundColor Yellow
    exit 0
}

Write-Host "=== Checking hexagonal layer boundaries (heuristic) ===" -ForegroundColor Cyan

function Get-Packages([string]$project) {
    [regex]::Matches((Get-Content $project -Raw), 'PackageReference\s+Include="([^"]+)"') |
        ForEach-Object { $_.Groups[1].Value }
}

function Get-ProjectRefs([string]$project) {
    [regex]::Matches((Get-Content $project -Raw), 'ProjectReference\s+Include="([^"]+)"') |
        ForEach-Object { $_.Groups[1].Value }
}

# Domain: no package except OneOf, no project reference at all.
$domainProject = Join-Path $sourcePath "Huishoudplanner.Domain/Huishoudplanner.Domain.csproj"
if (Test-Path $domainProject) {
    foreach ($pkg in (Get-Packages $domainProject)) {
        if ($pkg -ne "OneOf") {
            $violations += "DOMAIN VIOLATION: package '$pkg' in Huishoudplanner.Domain; only OneOf is allowed"
        }
    }
    foreach ($ref in (Get-ProjectRefs $domainProject)) {
        $violations += "DOMAIN VIOLATION: project reference '$ref' in Huishoudplanner.Domain; Domain references nothing of ours"
    }
}

# Application: only Domain as project reference, no infrastructure package.
$appProject = Join-Path $sourcePath "Huishoudplanner.Application/Huishoudplanner.Application.csproj"
if (Test-Path $appProject) {
    foreach ($ref in (Get-ProjectRefs $appProject)) {
        if ($ref -notmatch "Huishoudplanner\.Domain") {
            $violations += "APPLICATION VIOLATION: project reference '$ref' in Huishoudplanner.Application; only Domain is allowed"
        }
    }
    foreach ($pkg in (Get-Packages $appProject)) {
        if ($pkg -match "^(MongoDB\.|Microsoft\.AspNetCore|QuestPDF|Microsoft\.Extensions\.AI|OpenTelemetry|Cronos)") {
            $violations += "APPLICATION VIOLATION: infrastructure package '$pkg' in Huishoudplanner.Application"
        }
    }
}

# Source scans.
$csFiles = Get-ChildItem -Path $sourcePath -Filter "*.cs" -Recurse |
    Where-Object { $_.FullName -notmatch "[\\/](bin|obj)[\\/]" }
foreach ($file in $csFiles) {
    $relative = $file.FullName.Replace($repoRoot.Path, "").TrimStart("\", "/")
    $inner = $relative -match "Huishoudplanner\.(Domain|Application)[\\/]"
    $lineNum = 0
    foreach ($line in (Get-Content $file.FullName)) {
        $lineNum++
        if ($line -match "catch\s*\(\s*Exception\s" -and $line -notmatch "when\s*\(") {
            $violations += "CATCH VIOLATION: unfiltered catch(Exception) at ${relative}:$lineNum; add a when (...) filter that names an infrastructure failure"
        }
        if ($inner -and $line -match "DateTime(Offset)?\.(Now|UtcNow)") {
            $violations += "CLOCK VIOLATION: DateTime.Now/UtcNow at ${relative}:$lineNum; inject TimeProvider"
        }
        if ($line -match "using\s+MediatR|IRequestHandler<") {
            $violations += "MEDIATR VIOLATION: MediatR at ${relative}:$lineNum; endpoints call a driving port directly"
        }
    }
}

Write-Host ""
if ($violations.Count -eq 0) {
    Write-Host "=== All boundaries clean: no violations found ===" -ForegroundColor Green
} else {
    Write-Host "=== POTENTIAL VIOLATIONS ($($violations.Count)): review (heuristic, advisory only) ===" -ForegroundColor Yellow
    foreach ($v in $violations) {
        Write-Host "  ! $v" -ForegroundColor Yellow
    }
}

exit 0
