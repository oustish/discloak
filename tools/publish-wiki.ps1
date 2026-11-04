#Requires -Version 7.3
<#
.SYNOPSIS
    Publishes docs\ to the project's GitHub wiki.

.DESCRIPTION
    docs\ is the source of the documentation; the wiki is a copy of it for reading on GitHub, with its sidebar
    (docs\_Sidebar.md). This replaces every page of the wiki with docs\*.md as committed: links between pages become
    wiki links ("Guide-Backing-Up.md#x" -> "Guide-Backing-Up#x"), links out of docs\ become full GitHub addresses,
    and a footer on every page says which version and commit the pages come from. Never edit the wiki itself: the
    next publish overwrites it. The wiki has to exist (its first page is created once on GitHub, Wiki › Create the
    first page). See CONTRIBUTING.md › Releasing.

.EXAMPLE
    .\tools\publish-wiki.ps1            # publish (docs\ must have no uncommitted changes, and the commit be on GitHub)
    .\tools\publish-wiki.ps1 -DryRun    # only write the pages into a folder, to look at them
#>
param(
    [string]$Wiki = 'https://github.com/oustish/discloak.wiki.git',
    [string]$Project = 'https://github.com/oustish/discloak',
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$root = (git rev-parse --show-toplevel).Trim()
$docs = Join-Path $root 'docs'
$commit = (git -C $root rev-parse HEAD).Trim()
$version = [regex]::Match((Get-Content -Raw (Join-Path $root 'discloak\Discloak.csproj')), '<Version>([^<]+)</Version>').Groups[1].Value
if (-not $DryRun) {
    if (git -C $root status --porcelain -- docs) { throw "docs\ has uncommitted changes: commit them first (the wiki names the commit it comes from)." }
    git -C $root fetch --quiet origin
    if (-not (git -C $root branch -r --contains $commit)) { throw "Commit $($commit.Substring(0, 7)) is not on GitHub yet: push it first (the wiki links to it)." }
}

$work = Join-Path ([IO.Path]::GetTempPath()) "discloak-wiki-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
if ($DryRun) { New-Item -ItemType Directory $work | Out-Null }
else {
    git clone --quiet $Wiki $work
    Get-ChildItem $work -Filter *.md | Remove-Item
}

$pages = Get-ChildItem $docs -Filter *.md | ForEach-Object BaseName

# [text](target): a page of docs\ becomes a wiki link, anything else of the repository a link to it on GitHub
$link = [regex]'\]\((?<target>[^)\s]+)\)'
function Convert-Link([System.Text.RegularExpressions.Match]$m) {
    $target = $m.Groups['target'].Value
    if ($target -match '^(https?:|mailto:|#)') { return $m.Value }
    $path, $anchor = $target -split '#', 2
    $anchor = if ($anchor) { "#$anchor" } else { '' }
    $name = [IO.Path]::GetFileNameWithoutExtension($path)
    if ($path -notmatch '[\\/]' -and $path.EndsWith('.md') -and $pages -contains $name) { return "]($name$anchor)" }
    $full = [IO.Path]::GetFullPath((Join-Path $docs $path))
    if (-not (Test-Path $full)) { throw "A link in docs\ goes nowhere: $target" }
    $inRepo = [IO.Path]::GetRelativePath($root, $full).Replace('\', '/')
    $kind = if (Test-Path $full -PathType Container) { 'tree' } else { 'blob' }
    return "]($Project/$kind/master/$inRepo$anchor)"
}

$utf8 = [Text.UTF8Encoding]::new($false)
foreach ($page in Get-ChildItem $docs -Filter *.md) {
    $text = $link.Replace([IO.File]::ReadAllText($page.FullName), { param($m) Convert-Link $m })
    [IO.File]::WriteAllText((Join-Path $work $page.Name), $text.Replace("`r`n", "`n"), $utf8)
}
$footer = "Published from [``docs/``]($Project/tree/master/docs) of Discloak $version " +
          "(commit [$($commit.Substring(0, 7))]($Project/commit/$commit)). Edit the pages there: this wiki is overwritten by every publish.`n"
[IO.File]::WriteAllText((Join-Path $work '_Footer.md'), $footer, $utf8)

if ($DryRun) {
    Write-Host "Pages written to $work (nothing published)."
    return
}
try {
    git -C $work add -A
    if (-not (git -C $work status --porcelain)) { Write-Host 'The wiki already shows these pages: nothing to publish.'; return }
    $name = (git -C $root config user.name).Trim(); $email = (git -C $root config user.email).Trim()
    git -C $work -c "user.name=$name" -c "user.email=$email" commit --quiet -m "Publish docs/ of Discloak $version ($($commit.Substring(0, 7)))"
    git -C $work push --quiet origin HEAD
    Write-Host "Published $(@(Get-ChildItem $work -Filter *.md).Count) pages to $Wiki"
}
finally { Remove-Item -Recurse -Force $work }
