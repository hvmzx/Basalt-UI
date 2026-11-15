# Installs the built app to a normal Windows location and starts it.
#
# Why an install step: executables that live inside a protected/sandboxed
# development workspace (like this project folder) are refused tray-icon
# registration by Windows 11 on some setups — Shell_NotifyIcon returns
# ACCESS_DENIED and the icon silently never appears, no matter what the app
# does. A copy in %LocalAppData%\Programs registers fine. So: build here,
# run from there.
#
# Usage:  powershell -ExecutionPolicy Bypass -File install.ps1 [-Launch]

param([switch]$Launch)

$ErrorActionPreference = "Stop"

$source = Join-Path $PSScriptRoot "src\BasaltUI\bin\Release\net10.0-windows"
if (-not (Test-Path (Join-Path $source "BasaltUI.exe"))) {
    Write-Host "No Release build found. Building…"
    dotnet build (Join-Path $PSScriptRoot "src\BasaltUI\BasaltUI.csproj") -c Release
}

$dest = Join-Path $env:LOCALAPPDATA "Programs\BasaltUI"

# a running copy holds the exe open; close it first (it is ours to close)
Get-Process BasaltUI -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800

New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item (Join-Path $source "*") $dest -Recurse -Force

Write-Host "Installed to $dest"

if ($Launch) {
    Start-Process (Join-Path $dest "BasaltUI.exe")
    Write-Host "Started. The tray icon is in the taskbar corner overflow — drag it out if you want it beside the clock."
}
