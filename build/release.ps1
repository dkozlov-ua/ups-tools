<#
.SYNOPSIS
    Builds UPS Tools locally and publishes it as a GitHub release.

.DESCRIPTION
    The version comes from <Version> in Directory.Build.props: bump it, commit, then run this script.
    Steps: check the tree is clean and the tag is new -> run tests -> publish a framework-dependent
    single-file exe -> zip it -> tag vX.Y.Z and push the tag -> create the GitHub release with both files.

.PARAMETER DryRun
    Build and package only; no tag, no push, no release.

.EXAMPLE
    ./build/release.ps1            # release the version in Directory.Build.props
    ./build/release.ps1 -DryRun    # just build the artifacts
#>
param(
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function Invoke-Checked([string]$what, [scriptblock]$command) {
    Write-Host "==> $what" -ForegroundColor Cyan
    & $command
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit code $LASTEXITCODE)" }
}

[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = ($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
if (-not $version) { throw 'No <Version> found in Directory.Build.props' }
$tag = "v$version"
Write-Host "Releasing UPS Tools $version ($tag)" -ForegroundColor Green

if (-not $DryRun) {
    if (git status --porcelain) { throw 'Working tree is not clean; commit or stash first.' }
    git fetch --tags --quiet
    if (git tag --list $tag) { throw "Tag $tag already exists; bump <Version> in Directory.Build.props." }
}

Invoke-Checked 'Run tests' { dotnet test --configuration Release --nologo }

$out = Join-Path $root "artifacts\$version"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
$publish = Join-Path $out 'publish'

Invoke-Checked 'Publish' {
    dotnet publish src/UpsTools --configuration Release --runtime win-x64 --self-contained false `
        -p:PublishSingleFile=true -p:DebugType=None -p:GenerateDocumentationFile=false `
        --output $publish --nologo
}

$exe = Join-Path $out "UpsTools-$version-win-x64.exe"
$zip = Join-Path $out "UpsTools-$version-win-x64.zip"
Copy-Item (Join-Path $publish 'UpsTools.exe') $exe
Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $zip
Get-ChildItem $exe, $zip | Format-Table Name, @{ n = 'Size (MB)'; e = { [math]::Round($_.Length / 1MB, 1) } } -AutoSize

if ($DryRun) {
    Write-Host "Dry run: artifacts are in $out" -ForegroundColor Yellow
    return
}

Invoke-Checked "Tag $tag" { git tag -a $tag -m "UPS Tools $version" }
Invoke-Checked 'Push tag' { git push origin $tag }

$notes = @"
## Install

1. Install the [.NET 10 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/10.0) if you don't have it.
2. Download ``UpsTools-$version-win-x64.exe`` and run it. No installer; settings live in ``%APPDATA%\UpsTools``.

To upgrade, exit the running copy (tray icon → Exit, or ``UpsTools.exe --exit``) and replace the exe.
"@

Invoke-Checked 'Create GitHub release' {
    gh release create $tag $exe $zip --title "UPS Tools $version" --notes $notes --generate-notes --verify-tag
}
Write-Host "Done: $tag released." -ForegroundColor Green
