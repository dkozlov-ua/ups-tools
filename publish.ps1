<#
    publish.ps1 - package this commit and publish it as a GitHub release.

      powershell -File publish.ps1                  the version in VERSION
      powershell -File publish.ps1 -Version 1.2.0   must still match VERSION

    The release is the repository's LATEST release (not a draft, not a
    prerelease), tagged vX.Y.Z, carrying UpsTools-X.Y.Z.zip. The notes are
    docs\release-notes\vX.Y.Z.md. See RELEASE.md for the whole runbook.

    It refuses - before building anything - unless:
      - the working tree is clean, untracked files included,
      - HEAD is on origin's main (pushed), since the tag is created there,
      - VERSION says the version being published,
      - the release notes exist,
      - no release or tag of that version exists yet,
      - the app's "Project on GitHub" link (ProjectUrl in ConfigurationView.xaml.cs)
        is the repository being published to.

    It pushes nothing of its own: the release is created on GitHub with gh,
    which creates the tag there, and the tag is then fetched.
#>
[CmdletBinding()]
param(
    [string] $Version = ''
)

$ErrorActionPreference = 'Stop'
$RepoRoot = (Resolve-Path -LiteralPath $PSScriptRoot).Path

# A native command whose stderr is not wanted. Windows PowerShell turns a redirected
# stderr line into an error record, which under 'Stop' ends the script - so "release
# not found", the answer hoped for, would. $LASTEXITCODE is the answer either way.
function Native([scriptblock]$command) {
    $ErrorActionPreference = 'Continue'
    & $command 2>$null
}

function Invoke-Git() {
    $a = $args
    $out = Native { git.exe -C $RepoRoot @a }
    if ($LASTEXITCODE -ne 0) { throw "git $($a -join ' ') failed ($LASTEXITCODE)" }
    return $out
}

# ---- what is being published ------------------------------------------------
$fileVersion = (Get-Content -LiteralPath (Join-Path $RepoRoot 'VERSION') -TotalCount 1).Trim()
if (-not $Version) { $Version = $fileVersion }
$Version = $Version -replace '^v', ''
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "version '$Version' is not X.Y.Z" }
if ($Version -ne $fileVersion) { throw "VERSION says $fileVersion, not $Version - bump it, commit, push, then publish" }
$tag   = "v$Version"
$notes = Join-Path $RepoRoot "docs\release-notes\$tag.md"
if (-not (Test-Path -LiteralPath $notes)) { throw "no release notes at docs\release-notes\$tag.md" }

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw 'the GitHub CLI (gh) is not installed' }
$repo = Native { gh repo view --json nameWithOwner -q .nameWithOwner }
if ($LASTEXITCODE -ne 0 -or -not $repo) { throw 'gh cannot see this repository - gh auth login?' }

# The app links to its repository; it must be the one this publishes to.
$src = [IO.File]::ReadAllText((Join-Path $RepoRoot 'src\UpsTools\Views\ConfigurationView.xaml.cs'))
if ($src -notmatch 'ProjectUrl\s*=\s*"https://github\.com/([^"]+)"') { throw 'ConfigurationView.xaml.cs has no ProjectUrl line' }
if ($Matches[1] -ne $repo) {
    throw "the app links to $($Matches[1]), but this publishes to $repo"
}

# ---- is this commit the one to publish --------------------------------------
# Untracked files too: a clean tree is what makes the release exactly this commit.
$dirty = @(Invoke-Git status --porcelain)
if ($dirty) { throw "uncommitted changes - commit them (or ignore them) first:`n$($dirty -join "`n")" }
Invoke-Git fetch --quiet origin main --tags | Out-Null
$sha = (Invoke-Git rev-parse HEAD).Trim()
Native { git.exe -C $RepoRoot merge-base --is-ancestor $sha origin/main } | Out-Null
if ($LASTEXITCODE -ne 0) { throw "HEAD ($($sha.Substring(0, 7))) is not on origin/main - push it first" }
Native { git.exe -C $RepoRoot rev-parse -q --verify "refs/tags/$tag" } | Out-Null
if ($LASTEXITCODE -eq 0) { throw "tag $tag already exists" }
Native { gh release view $tag --repo $repo } | Out-Null
if ($LASTEXITCODE -eq 0) { throw "release $tag already exists on $repo" }

# ---- build ------------------------------------------------------------------
& (Join-Path $RepoRoot 'package.ps1') -Version $Version
$zip = Join-Path $RepoRoot "Releases\UpsTools-$Version.zip"
$sha256 = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()

# ---- publish ----------------------------------------------------------------
Write-Host "--- Publishing $tag to $repo at $($sha.Substring(0, 7)) ---"
& gh release create $tag $zip --repo $repo --target $sha --title "UPS Tools $Version" --notes-file $notes --latest
if ($LASTEXITCODE -ne 0) { throw "gh release create failed ($LASTEXITCODE)" }
Invoke-Git fetch --quiet origin --tags | Out-Null

# GitHub's own digest of the asset must be the zip that was built.
# Filtered here, not with -q: Windows PowerShell 5.1 strips the double quotes
# inside a native argument, so a jq filter naming ".zip" reaches gh as .zip and
# fails ("expected an object but got: string").
$assets = (& gh release view $tag --repo $repo --json assets) | ConvertFrom-Json
$digest = @($assets.assets | Where-Object { $_.name -like '*.zip' } | ForEach-Object { $_.digest })[0]
if ($digest -ne "sha256:$sha256") {
    Write-Warning "GitHub reports the asset as '$digest', but the zip built here is sha256:$sha256"
    exit 1
}
Write-Host "`nPublished UPS Tools ${Version}: https://github.com/$repo/releases/tag/$tag"
