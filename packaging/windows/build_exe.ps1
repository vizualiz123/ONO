param(
    [switch]$Clean,
    [switch]$Launcher  # legacy: build only the small launcher exe (depends on .venv)
)

$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$ProjectRoot = Resolve-Path (Join-Path $ScriptDir "..\..")
$Python = Join-Path $ProjectRoot ".venv\Scripts\python.exe"
$LauncherOnly = [bool]$Launcher
$LauncherScript = Join-Path $ScriptDir "nein3d_launcher.py"
$Spec = Join-Path $ScriptDir "nein3d.spec"
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

function Ensure-PythonPackage {
    param(
        [string]$ImportName,
        [string]$PackageName
    )

    $Check = Start-Process `
        -FilePath $Python `
        -ArgumentList @("-c", "import importlib.util, sys; sys.exit(0 if importlib.util.find_spec('$ImportName') else 1)") `
        -Wait `
        -PassThru `
        -WindowStyle Hidden

    if ($Check.ExitCode -ne 0) {
        & $Python -m pip install $PackageName
    }
}

Ensure-PythonPackage -ImportName "PyInstaller" -PackageName "pyinstaller"
Ensure-PythonPackage -ImportName "webview" -PackageName "pywebview"

$PyInstallerArgs = @(
    "-m", "PyInstaller",
    "--noconfirm",
    "--distpath", $DistRoot,
    "--workpath", $BuildRoot
)
if ($Clean) {
    $PyInstallerArgs += "--clean"
}

if ($LauncherOnly) {
    # Legacy small-launcher build (the exe still needs the project tree + .venv next to it).
    $PyInstallerArgs += @(
        "--onefile",
        "--console",
        "--name", "Nein3D",
        "--specpath", $BuildRoot,
        $LauncherScript
    )
} else {
    # Portable build via spec: bundles kimodo + viser + gradio + transformers + torch.
    $PyInstallerArgs += $Spec
}

& $Python @PyInstallerArgs

if ($LASTEXITCODE -ne 0) {
    throw "PyInstaller build failed."
}

Write-Host ""
if ($LauncherOnly) {
    Write-Host "Built launcher: $(Join-Path $DistRoot 'Nein3D.exe')"
} else {
    Write-Host "Built portable: $(Join-Path $DistRoot 'Nein3D\Nein3D.exe')"
}
