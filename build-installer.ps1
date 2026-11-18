# Builds the Basalt UI installer: publishes the single-file exe, then compiles the
# Inno Setup script into one setup exe. Plain ASCII so Windows PowerShell 5.1 reads it right.
#
# Usage:  powershell -ExecutionPolicy Bypass -File build-installer.ps1 [-Version 0.1.0] [-SkipPublish]

param(
    [string]$Version,
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

if (-not $Version) {
    $latest = git tag --list 'v*' | Sort-Object { [version]$_.TrimStart('v') } | Select-Object -Last 1
    $Version = if ($latest) { $latest.TrimStart('v') } else { '0.1.0' }
}

# Inno Setup is on PATH on GitHub's Windows runners; locally it can be in the user install dir.
$iscc = Get-Command iscc -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source
if (-not $iscc) {
    $candidates = @(
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    )
    $iscc = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $iscc) {
    throw "Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup"
}
"using ISCC: $iscc"

$publish = Join-Path $root 'publish'
if (-not $SkipPublish) {
    "publishing BasaltUI.exe $Version ..."
    dotnet publish (Join-Path $root 'src\BasaltUI\BasaltUI.csproj') -c Release -r win-x64 `
        --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        "-p:Version=$Version" -o $publish
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
}
if (-not (Test-Path (Join-Path $publish 'BasaltUI.exe'))) {
    throw "no publish/BasaltUI.exe - run without -SkipPublish first"
}

$artifacts = Join-Path $root 'artifacts'
New-Item $artifacts -ItemType Directory -Force | Out-Null

"compiling installer $Version ..."
& $iscc ("/DMyAppVersion=$Version") (Join-Path $root 'installer\BasaltUI.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }

Get-ChildItem $artifacts -Filter "BasaltUI-$Version-setup.exe" | ForEach-Object {
    "built: $($_.FullName) ($([math]::Round($_.Length / 1MB, 1)) MB)"
}
