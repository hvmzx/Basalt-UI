# Builds a release body from Conventional Commits, grouped by type, for the tag being released.
# Plain ASCII so Windows PowerShell 5.1 reads it right.
#
# Usage: powershell -ExecutionPolicy Bypass -File changelog.ps1 -Tag v0.1.0 -Out changelog.md

param(
    [Parameter(Mandatory = $true)][string]$Tag,
    [string]$Repo = 'hvmzx/Basalt-UI',
    [string]$Out = 'changelog.md'
)

$ErrorActionPreference = 'Stop'

$types = [ordered]@{
    'feat'     = 'Features'
    'fix'      = 'Bug Fixes'
    'perf'     = 'Performance Improvements'
    'refactor' = 'Code Refactoring'
    'docs'     = 'Documentation'
    'ci'       = 'Build & CI'
    'build'    = 'Build & CI'
    'style'    = 'Styling'
    'test'     = 'Tests'
    'chore'    = 'Other Changes'
}

$allTags = @(git tag --list 'v*' --sort=-v:refname)
$previous = $allTags | Where-Object { $_ -ne $Tag } | Select-Object -First 1
$range = if ($previous) { "$previous..$Tag" } else { $Tag }
$entries = @(git log --format='%H%x09%s' $range)

$groups = [ordered]@{}
foreach ($name in $types.Values) { $groups[$name] = [System.Collections.Generic.List[string]]::new() }
$breaks = [System.Collections.Generic.List[string]]::new()
$url = "https://github.com/$Repo/commit/"

foreach ($entry in $entries) {
    $parts = $entry -split "`t", 2
    if ($parts.Count -lt 2) { continue }
    $hash = $parts[0]
    $subject = $parts[1].Trim()
    if ($subject -match '^Merge\s') { continue }

    $m = [regex]::Match($subject, '^([a-zA-Z]+)(?:\(([^)]+)\))?(!)?: (.+)$')
    if (-not $m.Success) {
        $groups['Other Changes'].Add("- $subject ([$($hash.Substring(0,7))]($url$hash))")
        continue
    }

    $type = $m.Groups[1].Value.ToLowerInvariant()
    $scope = $m.Groups[2].Value
    $breaking = $m.Groups[3].Success
    $text = $m.Groups[4].Value.Trim()
    if ($scope.Length -gt 0) { $text = "**${scope}:** $text" }
    $line = "- $text ([$($hash.Substring(0,7))]($url$hash))"

    if ($breaking) { $breaks.Add($line) }
    elseif ($types.Contains($type)) { $groups[$types[$type]].Add($line) }
    else { $groups['Other Changes'].Add($line) }
}

$md = New-Object System.Text.StringBuilder
if ($breaks.Count -gt 0) {
    [void]$md.AppendLine('## Breaking Changes')
    [void]$md.AppendLine()
    foreach ($line in $breaks) { [void]$md.AppendLine($line) }
    [void]$md.AppendLine()
}
foreach ($name in $groups.Keys) {
    $lines = $groups[$name]
    if ($lines.Count -eq 0) { continue }
    [void]$md.AppendLine("## $name")
    [void]$md.AppendLine()
    foreach ($line in $lines) { [void]$md.AppendLine($line) }
    [void]$md.AppendLine()
}
if ($previous) {
    [void]$md.AppendLine("**Full Changelog**: https://github.com/$Repo/compare/$previous...$Tag")
}

$full = [System.IO.Path]::GetFullPath($Out)
[System.IO.File]::WriteAllText($full, $md.ToString(), (New-Object System.Text.UTF8Encoding($false)))
Write-Host "wrote $full"
