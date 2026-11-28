<#
    package-android.ps1 - build the UPS Tools APK for Android.

      powershell -File package-android.ps1                  the version in VERSION
      ... -Version 1.2.0                                    must still match VERSION (publish.ps1 passes it)
      ... -OutDir <dir>                                     somewhere other than Releases\
      ... -SkipTests                                        not the tests first (a quick local try)
      ... -Install                                          then install it on the phone adb sees
      ... -AndroidSdk <dir> -Jdk <dir>                      an SDK or JDK elsewhere

    Writes UpsTools-X.Y.Z-android.apk under OutDir (Releases\ by default,
    gitignored), built from the working tree: trimmed, compiled ahead of time,
    and signed with the app's own key from ~/.upstools-android (release.keystore
    and keystore-password, which chezmoi brings from the private configs repo).
    Every APK signed with that key updates the installed app, settings kept; one
    signed with any other key can't. So never make a new key: if it's missing,
    run 'chezmoi update'.

    -Install installs it with adb over USB or Wireless debugging (pair the phone
    once: Developer options -> Wireless debugging -> Pair device with pairing
    code, then 'adb pair <ip>:<port> <code>'). Monitoring starts again on its
    own after the update.

    NEEDS the .NET SDK with the android workload (dotnet workload install
    android), a JDK (17 or later) and the Android SDK. By default the JDK and
    the SDK are looked for under %LOCALAPPDATA%\Android; to install the SDK
    there:

      dotnet build src\UpsTools.Android -t:InstallAndroidDependencies -f net10.0-android `
          -p:AndroidSdkDirectory=$env:LOCALAPPDATA\Android\Sdk -p:JavaSdkDirectory=<jdk> `
          -p:AcceptAndroidSDKLicenses=True

    The Core tests and the Android app's own (tests\UpsTools.Android.Tests,
    which run on Windows) go first, so an APK whose protocol or event logic is
    broken is never written.

    This file is read by Windows PowerShell 5.1 in the ANSI codepage: keep it
    ASCII.
#>
[CmdletBinding()]
param(
    [string] $Version    = '',
    [string] $OutDir     = 'Releases',
    [string] $AndroidSdk = '',
    [string] $Jdk        = '',
    [switch] $SkipTests,
    [switch] $Install
)

$ErrorActionPreference = 'Stop'
$RepoRoot = (Resolve-Path -LiteralPath $PSScriptRoot).Path
$App      = Join-Path $RepoRoot 'src\UpsTools.Android\UpsTools.Android.csproj'

function Invoke-Checked([string]$what, [scriptblock]$command) {
    & $command
    if ($LASTEXITCODE -ne 0) { throw "$what failed ($LASTEXITCODE)" }
}

$fileVersion = (Get-Content -LiteralPath (Join-Path $RepoRoot 'VERSION') -TotalCount 1).Trim()
if (-not $Version) { $Version = $fileVersion }
$Version = $Version -replace '^v', ''
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "version '$Version' is not X.Y.Z" }
# The app takes its version from VERSION (Directory.Build.props): another one would be built under a wrong name.
if ($Version -ne $fileVersion) { throw "VERSION says $fileVersion, not $Version" }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'the .NET SDK (dotnet) is not installed' }

# ---- the SDK and the JDK ---------------------------------------------------
$Android = Join-Path $env:LOCALAPPDATA 'Android'
if (-not $AndroidSdk) { $AndroidSdk = if ($env:ANDROID_HOME) { $env:ANDROID_HOME } else { Join-Path $Android 'Sdk' } }
if (-not (Test-Path -LiteralPath (Join-Path $AndroidSdk 'platform-tools'))) {
    throw "no Android SDK in $AndroidSdk (see the top of this file to install one, or pass -AndroidSdk)"
}
if (-not $Jdk) {
    $Jdk = if ($env:JAVA_HOME) { $env:JAVA_HOME }
           else { Get-ChildItem -LiteralPath $Android -Directory -Filter 'jdk-*' -ErrorAction SilentlyContinue |
                  Sort-Object Name -Descending | Select-Object -First 1 -ExpandProperty FullName }
}
if (-not $Jdk -or -not (Test-Path -LiteralPath (Join-Path $Jdk 'bin\java.exe'))) {
    throw "no JDK found (looked in JAVA_HOME and $Android\jdk-*): pass -Jdk"
}

# ---- tests -----------------------------------------------------------------
if (-not $SkipTests) {
    Write-Host 'Running the tests...'
    Invoke-Checked 'the Core tests' { dotnet test (Join-Path $RepoRoot 'tests\UpsTools.Tests') --nologo -v q }
    Invoke-Checked 'the Android app tests' { dotnet test (Join-Path $RepoRoot 'tests\UpsTools.Android.Tests') --nologo -v q }
}

# ---- build -----------------------------------------------------------------
$Publish = Join-Path $RepoRoot 'src\UpsTools.Android\bin\Release\publish'
if (Test-Path -LiteralPath $Publish) { Remove-Item -LiteralPath $Publish -Recurse -Force }
Write-Host "Building UPS Tools $Version for Android..."
Invoke-Checked 'dotnet publish' {
    dotnet publish $App -c Release -f net10.0-android -o $Publish --nologo -v q `
        "-p:AndroidSdkDirectory=$AndroidSdk" "-p:JavaSdkDirectory=$Jdk"
}
$Apk = Get-ChildItem -LiteralPath $Publish -Filter '*-Signed.apk' | Select-Object -First 1
if (-not $Apk) { throw "dotnet publish wrote no signed APK in $Publish" }

$Out = if ([IO.Path]::IsPathRooted($OutDir)) { $OutDir } else { Join-Path $RepoRoot $OutDir }
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$Target = Join-Path $Out "UpsTools-$Version-android.apk"
Copy-Item -LiteralPath $Apk.FullName -Destination $Target -Force
$Size = [Math]::Round((Get-Item -LiteralPath $Target).Length / 1MB, 1)
Write-Host "Wrote $Target ($Size MB)"

# ---- install ---------------------------------------------------------------
if ($Install) {
    $adb = Join-Path $AndroidSdk 'platform-tools\adb.exe'
    $devices = @(& $adb devices | Select-Object -Skip 1 | Where-Object { $_ -match '\tdevice$' })
    if ($devices.Count -ne 1) {
        throw "adb sees $($devices.Count) phones; connect exactly one (USB debugging, or Wireless debugging and 'adb connect')"
    }
    Write-Host "Installing on $(($devices[0] -split "`t")[0])..."
    Invoke-Checked 'adb install' { & $adb install -r $Target }
}
