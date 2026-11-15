# What the app does, against the basalt you have installed — see README.md.
#   powershell -ExecutionPolicy Bypass -File tools\selftest.ps1
#
# Nothing here stops a running server: the live one only gets GET requests, and the start/stop test
# runs its own throwaway basalt on port 18085 with --mock-engine (no model file, no VRAM).
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$env:DOTNET_CLI_HOME = Join-Path $root '.dotnet-home'
$env:NUGET_PACKAGES  = Join-Path $root '.nuget-packages'
$env:BASALTUI_HOME   = Join-Path $root '.scratch\appdata'      # keep backups and scratch out of %APPDATA%
$env:DOTNET_NOLOGO   = '1'

$probe = Join-Path $root 'tools\Probe\bin\Release\net10.0-windows\basalt-probe.exe'
if (-not (Test-Path $probe)) {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        Write-Host '.NET 10 SDK (dotnet) is not on the PATH, and there is no built probe to run.'
        exit 1
    }
    Write-Host 'building the probe …'
    dotnet build (Join-Path $root 'tools\Probe\Probe.csproj') -c Release -v q --nologo
    if (-not (Test-Path $probe)) { Write-Host "build did not produce $probe"; exit 1 }
}

# The config file it reads is %USERPROFILE%\Documents\basalt_config.json, the probe's own default;
# to check another one:  .\tools\Probe\bin\Release\net10.0-windows\basalt-probe.exe --tree --serve D:\other.json

$report = Join-Path $root ("selftest-{0}.txt" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
& $probe --tree --serve *>&1 | Tee-Object -FilePath $report

Write-Host ''
Write-Host "report kept in $report"
