<#
    package.ps1 - build the UPS Tools setup.

      powershell -File package.ps1                     the version in VERSION
      powershell -File package.ps1 -Version 1.2.0      another one (publish.ps1 passes it)
      ... -OutDir <dir>                                somewhere other than Releases\
      ... -SkipTests                                   not the test suite first (a quick local try)

    Writes UpsTools-X.Y.Z-setup.exe under OutDir (Releases\ by default,
    gitignored): the one asset a GitHub release carries.

    WHAT GOES IN is the app as `dotnet publish` builds it - one framework-dependent
    UpsTools.exe for win-x64 (it needs the .NET 10 Desktop Runtime) - compiled
    into a setup by Inno Setup 6 from installer\UpsTools.iss. It is built from
    the working tree, so a change can be packaged and installed before it is
    committed; that is said, and publish.ps1 refuses it outright.

    NEEDS the .NET SDK and Inno Setup 6 (winget install JRSoftware.InnoSetup).

    The test suite runs before anything is built, so a setup whose protocol or
    event logic is broken is never written.

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

# The Inno Setup compiler: on PATH, or where its installer put it (per user or for everyone).
function Find-Iscc() {
    $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $keys = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1',
            'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1',
            'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1'
    foreach ($key in $keys) {
        $dir = (Get-ItemProperty -Path $key -ErrorAction SilentlyContinue).InstallLocation
        if ($dir -and (Test-Path -LiteralPath (Join-Path $dir 'ISCC.exe'))) { return (Join-Path $dir 'ISCC.exe') }
    }
    throw 'Inno Setup 6 is not installed - winget install JRSoftware.InnoSetup'
}
$Iscc = Find-Iscc

# ---- where it goes ---------------------------------------------------------
if (-not [IO.Path]::IsPathRooted($OutDir)) { $OutDir = Join-Path $RepoRoot $OutDir }
$OutDir = [IO.Path]::GetFullPath($OutDir)
if ($OutDir.TrimEnd('\') -eq $RepoRoot.TrimEnd('\')) { throw 'OutDir must not be the repository root' }
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
$name  = "UpsTools-$Version-setup"
$setup = Join-Path $OutDir "$name.exe"

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
Write-Host "--- Building UpsTools.exe $Version ---"
Invoke-Checked 'dotnet publish' {
    dotnet publish $App --configuration Release --runtime win-x64 --self-contained false `
        -p:PublishSingleFile=true -p:DebugType=None -p:Version=$Version --output $publish --nologo
}
$exe = Join-Path $publish 'UpsTools.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'dotnet publish did not produce UpsTools.exe' }
$extra = @(Get-ChildItem -LiteralPath $publish -File | Where-Object { $_.Name -ne 'UpsTools.exe' })
if ($extra) { throw "dotnet publish produced more than the single exe ($($extra.Name -join ', ')) - the setup would miss them" }

# ---- the setup -------------------------------------------------------------
Write-Host "--- Compiling $name.exe ---"
if (Test-Path -LiteralPath $setup) { Remove-Item -LiteralPath $setup -Force }
Invoke-Checked 'ISCC' {
    & $Iscc /Q "/DVersion=$Version" "/DAppExe=$exe" "/O$OutDir" (Join-Path $RepoRoot 'installer\UpsTools.iss')
}
Remove-Item -LiteralPath $publish -Recurse -Force
if (-not (Test-Path -LiteralPath $setup -PathType Leaf)) { throw "ISCC did not write $setup" }
$sha = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()

Write-Host "`nPackaged UPS Tools $Version"
Write-Host "  $setup"
Write-Host "  sha256 $sha"
