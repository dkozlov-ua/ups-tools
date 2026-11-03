<#
    package.ps1 - build a release package of UPS Tools.

      powershell -File package.ps1                     the version in VERSION
      powershell -File package.ps1 -Version 1.2.0      another one (publish.ps1 passes it)
      ... -OutDir <dir>                                somewhere other than Releases\
      ... -SkipTests                                   not the test suite first (a quick local try)

    Writes, under OutDir (Releases\ by default, gitignored):

      UpsTools-X.Y.Z\        the package, staged: what install.ps1 installs
      UpsTools-X.Y.Z.zip     the same, zipped - the one asset a GitHub release carries

    WHAT GOES IN is the app as `dotnet publish` builds it - one framework-dependent
    UpsTools.exe for win-x64 (it needs the .NET 10 Desktop Runtime) - and
    install.ps1. It is built from the working tree, so a change can be packaged
    and installed before it is committed; that is said, and publish.ps1 refuses
    it outright. Two files are the package's own:

      VERSION        the version, one line
      manifest.txt   every other file in the package, one relative path per
                     line - what install.ps1 copies, and what the NEXT install
                     reads to know which files are its own to replace or delete

    The test suite runs before anything is built, so a package whose protocol
    or event logic is broken is never written.

    This file is read by Windows PowerShell 5.1 in the ANSI codepage: keep it
    ASCII.
#>
[CmdletBinding()]
param(
    [string] $Version = '',
    [string] $OutDir  = 'Releases',
    [switch] $SkipTests
)

$ErrorActionPreference = 'Stop'
$RepoRoot = (Resolve-Path -LiteralPath $PSScriptRoot).Path
$App      = Join-Path $RepoRoot 'src\UpsTools\UpsTools.csproj'

function Invoke-Checked([string]$what, [scriptblock]$command) {
    & $command
    if ($LASTEXITCODE -ne 0) { throw "$what failed ($LASTEXITCODE)" }
}

# ---- the version -----------------------------------------------------------
if (-not $Version) { $Version = (Get-Content -LiteralPath (Join-Path $RepoRoot 'VERSION') -TotalCount 1).Trim() }
$Version = $Version -replace '^v', ''
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "version '$Version' is not X.Y.Z"
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'the .NET SDK (dotnet) is not installed' }

# ---- where it goes ---------------------------------------------------------
if (-not [IO.Path]::IsPathRooted($OutDir)) { $OutDir = Join-Path $RepoRoot $OutDir }
$OutDir = [IO.Path]::GetFullPath($OutDir)
if ($OutDir.TrimEnd('\') -eq $RepoRoot.TrimEnd('\')) { throw 'OutDir must not be the repository root' }
$name  = "UpsTools-$Version"
$stage = Join-Path $OutDir $name
$zip   = Join-Path $OutDir "$name.zip"

$dirty = @(git -C $RepoRoot status --porcelain)
if ($dirty) { Write-Warning "packaging what is not committed:`n$($dirty -join "`n")" }

# ---- does it work ----------------------------------------------------------
if (-not $SkipTests) {
    Write-Host '--- Testing ---'
    Invoke-Checked 'dotnet test' { dotnet test (Join-Path $RepoRoot 'UpsTools.slnx') --configuration Release --nologo -p:Version=$Version }
}

# ---- build -----------------------------------------------------------------
$publish = Join-Path $OutDir ".publish-$Version"
if (Test-Path -LiteralPath $publish) { Remove-Item -LiteralPath $publish -Recurse -Force }
Write-Host "--- Building $name ---"
Invoke-Checked 'dotnet publish' {
    dotnet publish $App --configuration Release --runtime win-x64 --self-contained false `
        -p:PublishSingleFile=true -p:DebugType=None -p:Version=$Version --output $publish --nologo
}

# ---- what goes in ----------------------------------------------------------
$files = [ordered]@{
    'UpsTools.exe' = Join-Path $publish 'UpsTools.exe'
    'install.ps1'  = Join-Path $RepoRoot 'install.ps1'
}
foreach ($rel in $files.Keys) {
    if (-not (Test-Path -LiteralPath $files[$rel] -PathType Leaf)) { throw "$rel is missing - a package without it would not install or run" }
}
$extra = @(Get-ChildItem -LiteralPath $publish -File | Where-Object { $_.Name -ne 'UpsTools.exe' })
if ($extra) { throw "dotnet publish produced more than the single exe ($($extra.Name -join ', ')) - the package would miss them" }

Write-Host "--- Staging $name ($($files.Count) files) ---"
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage -Force | Out-Null
foreach ($rel in $files.Keys) { Copy-Item -LiteralPath $files[$rel] -Destination (Join-Path $stage $rel) }
Remove-Item -LiteralPath $publish -Recurse -Force

$utf8 = New-Object Text.UTF8Encoding($false)
[IO.File]::WriteAllText((Join-Path $stage 'VERSION'), "$Version`n", $utf8)
$manifest = @($files.Keys) + 'VERSION'
[IO.File]::WriteAllText((Join-Path $stage 'manifest.txt'), (($manifest | Sort-Object) -join "`n") + "`n", $utf8)

# ---- the zip ---------------------------------------------------------------
Add-Type -AssemblyName System.IO.Compression.FileSystem
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
$sha = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()

Write-Host "`nPackaged $name"
Write-Host "  $zip"
Write-Host "  sha256 $sha"
