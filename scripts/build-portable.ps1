#Requires -Version 5.1
param(
    [string]$Version = "1.0.1",
    [switch]$SelfContained
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$repoRoot = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $repoRoot ".tools\dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) {
    $dotnet = "dotnet"
} else {
    $env:DOTNET_ROOT = Join-Path $repoRoot ".tools\dotnet"
    $env:NUGET_PACKAGES = Join-Path $repoRoot ".tools\nuget"
}

$distRoot = Join-Path $repoRoot "dist"
$flavor = if ($SelfContained) { "win-x64-standalone" } else { "win-x64-portable" }
$stageDir = Join-Path $distRoot "TahoeDesk-v$Version-$flavor"
$zipPath = "$stageDir.zip"

$wdArgs = @("build", (Join-Path $repoRoot "src\TahoeDesk.Watchdog\TahoeDesk.Watchdog.csproj"), "-c", "Release", "-m:1", "/nr:false", "--verbosity", "minimal")
if (Test-Path (Join-Path $repoRoot "src\TahoeDesk.Watchdog\obj\project.assets.json")) { $wdArgs += "--no-restore" }

Write-Host "[TahoeDesk] Building Watchdog (Release)..." -ForegroundColor Cyan
& $dotnet @wdArgs
if ($LASTEXITCODE -ne 0) { throw "Watchdog build failed." }

Write-Host "[TahoeDesk] Building TahoeDesk UI (Release)..." -ForegroundColor Cyan
if ($SelfContained) {
    & $dotnet publish (Join-Path $repoRoot "src\TahoeDesk.UI\TahoeDesk.UI.csproj") -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -o $stageDir --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw "Self-contained publish failed." }
} else {
    $uiArgs = @("build", (Join-Path $repoRoot "src\TahoeDesk.UI\TahoeDesk.UI.csproj"), "-c", "Release", "-m:1", "/nr:false", "--verbosity", "minimal")
    if (Test-Path (Join-Path $repoRoot "src\TahoeDesk.UI\obj\project.assets.json")) { $uiArgs += "--no-restore" }
    & $dotnet @uiArgs
    if ($LASTEXITCODE -ne 0) { throw "UI build failed." }

    if (Test-Path $stageDir) {
        Get-ChildItem -Path $stageDir -Force -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    } else {
        New-Item -ItemType Directory -Path $stageDir -Force | Out-Null
    }

    $releaseOut = Join-Path $repoRoot "src\TahoeDesk.UI\bin\Release\net8.0-windows10.0.22621.0"
    Copy-Item -Path (Join-Path $releaseOut "*") -Destination $stageDir -Recurse -Force
    # Clean any stale debug symbols or removed third-party DLLs from incremental builds
    Get-ChildItem -Path $stageDir -Recurse -Include *.pdb,CommunityToolkit.Mvvm.dll | Remove-Item -Force -ErrorAction SilentlyContinue
}

# Include emergency native taskbar recovery script + docs
$restoreCmd = @"
@echo off
title TahoeDesk - Restore Windows Taskbar
echo Restoring native Windows taskbar...
start "" "%~dp0TahoeDesk.exe" --restore-taskbar
"@
Set-Content -Path (Join-Path $stageDir "RestoreTaskbar.cmd") -Value $restoreCmd -Encoding ASCII

Copy-Item -Path (Join-Path $repoRoot "LICENSE") -Destination $stageDir -Force
Copy-Item -Path (Join-Path $repoRoot "README.md") -Destination $stageDir -Force

if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($stageDir, $zipPath, [System.IO.Compression.CompressionLevel]::Optimal, $false)

$zipSizeMB = [math]::Round((Get-Item $zipPath).Length / 1MB, 2)
Write-Host "[TahoeDesk] Portable package ready: $zipPath ($zipSizeMB MB)" -ForegroundColor Green
