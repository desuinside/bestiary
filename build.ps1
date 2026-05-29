#Requires -Version 5
<#
.SYNOPSIS
    Build iNat Bestiary as a self-contained Windows x64 executable via Docker.
.DESCRIPTION
    Uses a Linux Docker container to cross-compile to win-x64.
    Output goes to .\publish\  —  run InatBestiary.exe from there.
#>

$ErrorActionPreference = "Stop"
$env:DOCKER_BUILDKIT = "1"

Write-Host "Building..." -ForegroundColor Cyan

docker build --target export --output "type=local,dest=$PSScriptRoot\publish" $PSScriptRoot

if ($LASTEXITCODE -eq 0) {
    Write-Host ""
    Write-Host "Done. Output in: $PSScriptRoot\publish" -ForegroundColor Green
    Write-Host "Run: .\publish\InatBestiary.exe" -ForegroundColor Green
} else {
    Write-Host "Build failed (exit $LASTEXITCODE)" -ForegroundColor Red
    exit $LASTEXITCODE
}
