# Cuts a version: bumps the newest vX.Y.Z tag, pushes the branch and the tag.
# The release workflow does the rest — it builds the exe and publishes the release.
#
# Usage:  powershell -ExecutionPolicy Bypass -File release.ps1 [-Part major|minor|patch]

param([ValidateSet('major', 'minor', 'patch')][string]$Part = 'patch')

$ErrorActionPreference = "Stop"

if (git status --porcelain) {
    throw "the tree has uncommitted changes - commit them first"
}

$latest = git tag --list 'v*' |
    Sort-Object { [version]$_.TrimStart('v') } |
    Select-Object -Last 1

$tag = if ($latest) {
    $ver = [version]$latest.TrimStart('v')
    $next = switch ($Part) {
        'major' { [version]::new($ver.Major + 1, 0, 0) }
        'minor' { [version]::new($ver.Major, $ver.Minor + 1, 0) }
        default { [version]::new($ver.Major, $ver.Minor, [Math]::Max($ver.Build, 0) + 1) }
    }
    "v$next"
}
else { "v0.1.0" }

git tag -a $tag -m $tag
$branch = git rev-parse --abbrev-ref HEAD
git push origin $branch
git push origin $tag

Write-Host "tagged $tag and pushed - the workflow builds the release."
