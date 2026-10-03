#!/usr/bin/env pwsh
# Run the apps/api .NET test suites, with an optional layer filter.
# Integration tests start a mongo:8 single-node replica set through Testcontainers.MongoDb:
# Docker must be running, but there is nothing to start by hand.
# Usage:
#   ./run-tests.ps1                        # Whole solution
#   ./run-tests.ps1 -Layer domain          # Only Domain.Tests
#   ./run-tests.ps1 -Layer application     # Only Application.Tests
#   ./run-tests.ps1 -Layer architecture    # Only Architecture.Tests
#   ./run-tests.ps1 -Layer integration     # Only Integration.Tests (Testcontainers)
#   ./run-tests.ps1 -Coverage              # Whole solution + coverage report

param(
    [ValidateSet("all", "domain", "application", "architecture", "integration")]
    [string]$Layer = "all",

    [switch]$Coverage
)

$ErrorActionPreference = "Stop"

# This script lives at .agents/skills/xunit-tdd-workflow/scripts/: the repository root is four up.
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "../../../..")
$api = Join-Path $repoRoot "apps/api"

if (-not (Test-Path (Join-Path $api "Huishoudplanner.slnx"))) {
    Write-Host "apps/api/Huishoudplanner.slnx does not exist yet" -ForegroundColor Yellow
    exit 0
}

$target = switch ($Layer) {
    "domain"       { Join-Path $api "tests/Huishoudplanner.Domain.Tests/Huishoudplanner.Domain.Tests.csproj" }
    "application"  { Join-Path $api "tests/Huishoudplanner.Application.Tests/Huishoudplanner.Application.Tests.csproj" }
    "architecture" { Join-Path $api "tests/Huishoudplanner.Architecture.Tests/Huishoudplanner.Architecture.Tests.csproj" }
    "integration"  { Join-Path $api "tests/Huishoudplanner.Integration.Tests/Huishoudplanner.Integration.Tests.csproj" }
    default        { Join-Path $api "Huishoudplanner.slnx" }
}

Write-Host "=== Running $Layer tests: $target ===" -ForegroundColor Cyan

$testArgs = @("test", $target, "--verbosity", "minimal")

if ($Coverage) {
    # xunit.v3 on Microsoft Testing Platform: the coverage extension must be referenced by the
    # test projects; the flag below is the MTP spelling.
    $testArgs += @("--coverage", "--results-directory", (Join-Path $api "coverage"))
}

& dotnet @testArgs
if ($LASTEXITCODE -ne 0) {
    Write-Host "FAILED: $Layer tests" -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host "`n=== All requested tests passed ===" -ForegroundColor Green
