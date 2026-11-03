# Reads the version to release and its notes. The release workflow runs it, and so do the tests,
# so a push to product cannot reach the release step with a VERSION the CHANGELOG does not describe.
# Written for both Windows PowerShell 5.1 and PowerShell 7.
param(
    [string]$Root = '.',
    [string]$NotesPath = 'release-notes.md'
)

$ErrorActionPreference = 'Stop'

$version = (Get-Content -LiteralPath (Join-Path $Root 'VERSION') -Raw).Trim()
if ($version -notmatch '^\d\.\d\.\d\.\d$') {
    throw "VERSION '$version' is not four single-digit parts."
}

$changelog = Get-Content -LiteralPath (Join-Path $Root 'CHANGELOG.md') -Raw -Encoding UTF8
$pattern = '(?ms)^## ' + [regex]::Escape($version) + '[ \t]*\r?\n(.*?)(?=^## |\z)'
$section = [regex]::Match($changelog, $pattern)
$notes = if ($section.Success) { $section.Groups[1].Value.Trim() } else { '' }
if ($notes.Length -eq 0) {
    throw "CHANGELOG.md has no section for $version. Add '## $version' with what changed."
}

$out = if ([System.IO.Path]::IsPathRooted($NotesPath)) { $NotesPath } else { Join-Path (Get-Location) $NotesPath }
[System.IO.File]::WriteAllText($out, $notes + "`n", (New-Object System.Text.UTF8Encoding $false))

if ($env:GITHUB_OUTPUT) {
    Add-Content -LiteralPath $env:GITHUB_OUTPUT -Value "version=$version"
    Add-Content -LiteralPath $env:GITHUB_OUTPUT -Value "tag=v$version"
}

Write-Host "Version $version, notes in $NotesPath"
