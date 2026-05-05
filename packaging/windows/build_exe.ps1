param(
    [string]$Name = "Nein3D",
    [switch]$Clean
)

$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$ProjectRoot = Resolve-Path (Join-Path $ScriptDir "..\..")
$Python = Join-Path $ProjectRoot ".venv\Scripts\python.exe"
$Launcher = Join-Path $ScriptDir "nein3d_launcher.py"
$BuildRoot = Join-Path $ProjectRoot "build\pyinstaller"
$DistRoot = Join-Path $ProjectRoot "release\windows"

if (-not (Test-Path -LiteralPath $Python)) {
    throw "Python venv not found: $Python"
}

if ($Clean -and (Test-Path -LiteralPath $BuildRoot)) {
    Remove-Item -LiteralPath $BuildRoot -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $BuildRoot | Out-Null
New-Item -ItemType Directory -Force -Path $DistRoot | Out-Null

$Check = Start-Process `
    -FilePath $Python `
    -ArgumentList @("-c", "import importlib.util, sys; sys.exit(0 if importlib.util.find_spec('PyInstaller') else 1)") `
    -Wait `
    -PassThru `
    -WindowStyle Hidden

if ($Check.ExitCode -ne 0) {
    & $Python -m pip install pyinstaller
}

& $Python -m PyInstaller `
    --noconfirm `
    --clean `
    --onefile `
    --console `
    --name $Name `
    --distpath $DistRoot `
    --workpath $BuildRoot `
    --specpath $BuildRoot `
    $Launcher

if ($LASTEXITCODE -ne 0) {
    throw "PyInstaller build failed."
}

$Exe = Join-Path $DistRoot "$Name.exe"
if (-not (Test-Path -LiteralPath $Exe)) {
    throw "Build finished but exe was not found: $Exe"
}

Write-Host ""
Write-Host "Built: $Exe"
