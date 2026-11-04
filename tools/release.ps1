#Requires -Version 7.3
<#
.SYNOPSIS
    Builds the release files of a tagged version of Discloak.

.DESCRIPTION
    Builds the tag in a fresh worktree of its own (so nothing uncommitted can get in, and the exe's version is the
    tag's, without '*'), then writes release\<tag>\:
      discloak-<version>-win-x64.zip   discloak.exe (self-contained), its license files, CHANGELOG.md
      discloak-<version>-source.zip    git archive of the tag
      notes.md                         the version's section of CHANGELOG.md: the text of the GitHub release
    No checksum file: GitHub shows every release file's SHA-256 on the release page.
    The worktree is removed afterwards. Needs git and the .NET 10 SDK. See CONTRIBUTING.md › Versions.

.EXAMPLE
    .\tools\release.ps1 v0.9.0
#>
param([Parameter(Mandatory)][string]$Tag)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true   # a failing git or dotnet stops the script

$repo = (git rev-parse --show-toplevel).Trim()
if (-not (git -C $repo tag --list $Tag)) { throw "There is no tag '$Tag'." }
$version = $Tag.TrimStart('v')
$out = Join-Path $repo "release\$Tag"
$work = Join-Path ([IO.Path]::GetTempPath()) "discloak-release-$version-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
$branch = "release/$Tag"   # 'discloak version' of the built exe names it

if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory $out | Out-Null
if (git -C $repo branch --list $branch) { git -C $repo branch -D $branch | Out-Null }   # left over from a run that failed
git -C $repo worktree add -b $branch $work $Tag

try {
    Write-Host "Building $Tag in $work ..."
    dotnet publish (Join-Path $work 'discloak\Discloak.csproj') -p:PublishProfile=win-x64 -o (Join-Path $work 'out') -nologo -v quiet

    $exe = Join-Path $work 'out\discloak.exe'
    $built = (& $exe version) -join "`n"
    if ($built -notmatch "^discloak $([regex]::Escape($version)) \(Release\)" -or $built -match '\*') {
        throw "The built exe does not say version $version, or says it has uncommitted changes:`n$built"
    }

    # the exe with everything that goes with every copy of it (publish puts the license files next to it)
    $package = Join-Path $work 'package'
    New-Item -ItemType Directory $package | Out-Null
    Copy-Item (Join-Path $work 'out\*') $package
    Copy-Item (Join-Path $work 'CHANGELOG.md') $package
    Compress-Archive -Path (Join-Path $package '*') -DestinationPath (Join-Path $out "discloak-$version-win-x64.zip")

    git -C $repo archive --format=zip --prefix="discloak-$version/" -o (Join-Path $out "discloak-$version-source.zip") $Tag

    # the version's section of the changelog: from its heading to the next one
    $lines = Get-Content (Join-Path $work 'CHANGELOG.md')
    $start = [array]::FindIndex($lines, [Predicate[string]] { param($l) $l -match "^## $([regex]::Escape($version))( |$)" })
    if ($start -lt 0) { throw "CHANGELOG.md has no section for $version." }
    $end = [array]::FindIndex($lines, $start + 1, [Predicate[string]] { param($l) $l -match '^## ' })
    if ($end -lt 0) { $end = $lines.Count }
    $lines[($start + 1)..($end - 1)] -join "`n" | ForEach-Object Trim | Set-Content (Join-Path $out 'notes.md') -Encoding utf8

    Write-Host "`n$built`n"
    Get-ChildItem $out | Format-Table Name, Length -AutoSize
}
finally {
    git -C $repo worktree remove --force $work
    git -C $repo branch -D $branch | Out-Null
}
