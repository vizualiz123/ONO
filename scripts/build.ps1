param(
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"

$project = Join-Path $PSScriptRoot "..\apps\AvatarDesktop\AvatarDesktop.csproj"
$project = [System.IO.Path]::GetFullPath($project)

Write-Host "Checking .NET SDK..." -ForegroundColor Cyan
$sdks = & dotnet --list-sdks
if (-not $sdks) {
    throw ".NET SDK not found. Install .NET 8 SDK and retry."
}

Write-Host "Building $project ($Configuration)..." -ForegroundColor Cyan
& dotnet build $project -c $Configuration
if ($LASTEXITCODE -ne 0) {
    throw "Build failed."
}

Write-Host "Build completed." -ForegroundColor Green
