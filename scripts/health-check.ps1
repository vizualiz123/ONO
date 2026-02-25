param(
    [string]$BaseUrl = "http://127.0.0.1:1234/v1"
)

$ErrorActionPreference = "Stop"

$endpoint = ($BaseUrl.TrimEnd("/") + "/models")
Write-Host "LM Studio health check: $endpoint" -ForegroundColor Cyan

try {
    $response = Invoke-RestMethod -Uri $endpoint -Method Get -TimeoutSec 8
    Write-Host "OK: /models reachable" -ForegroundColor Green
    $response | ConvertTo-Json -Depth 10
}
catch {
    Write-Host "FAIL: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
