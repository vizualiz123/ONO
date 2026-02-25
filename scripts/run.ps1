param(
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"

$project = Join-Path $PSScriptRoot "..\apps\AvatarDesktop\AvatarDesktop.csproj"
$project = [System.IO.Path]::GetFullPath($project)

Write-Host "Running AvatarDesktop ($Configuration)..." -ForegroundColor Cyan
& dotnet run --project $project -c $Configuration
