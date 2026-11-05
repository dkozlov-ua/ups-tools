<#
    install.ps1 - install UPS Tools from a release package, update an
    installation in place, or remove one.

    INSTALL, from an extracted package (UpsTools-X.Y.Z.zip, unzipped):

      powershell -ExecutionPolicy Bypass -File install.ps1 [-InstallDir <dir>] [-NoShortcut] [-Force] [-Launch]

      Copies the package into -InstallDir - by default
      %LOCALAPPDATA%\Programs\UpsTools - and puts a "UPS Tools" shortcut in the
      Start menu. Run it again from a newer package to update the same folder;
      nothing outside the package's own files is touched.

    UPDATE, from a downloaded release zip, run from the installation it updates:

      install.ps1 -Package <zip> -Sha256 <hex> [-WaitPid <pid>] [-Launch]

      Verifies the zip against -Sha256, unzips it, and hands over to THE NEW
      VERSION'S install.ps1 with -InstallDir set to the folder this script is in -
      so an update always lands where the installation already is, and whatever
      points at its exe (the Start menu shortcut, a pin, Start with Windows)
      never needs repointing.

    UNINSTALL:

      install.ps1 -Uninstall [-InstallDir <dir>]

      Deletes the package's own files, the shortcut, the Start with Windows
      entry when it starts this installation, the app's notification
      registration and images (%LOCALAPPDATA%\UpsTools), and the scheduled
      tasks of scripts set to run as administrator that start this
      installation (\UpsTools in Task Scheduler; deleting them asks for
      administrator rights once). The settings and the log in
      %APPDATA%\UpsTools are the user's and are left.

    A RUNNING UPS TOOLS. The exe cannot be replaced while it runs, so a copy
    running from the target folder is asked to exit (the same signal as
    "UpsTools.exe --exit": it disconnects from the UPS cleanly) and waited for,
    up to 30s, before anything is changed. An install or update starts it again
    afterwards, in the tray, when it was running; -Launch starts it with its
    window. -WaitPid first waits (up to 30s) for that process to exit.

    Every decision goes to the app's own log, %APPDATA%\UpsTools\ups-tools.log.
    A failure under -Launch is also shown in a timed message box (the console
    may be hidden then) unless -Quiet. Exit codes: 0 done, 1 failed, 2 refused
    (bad arguments, or a folder this will not write into).

    WHICH FILES ARE ITS OWN. The package's manifest.txt lists every file in it.
    An install copies exactly those, and deletes a file the PREVIOUS install's
    manifest listed and this one does not. Nothing else in the folder is
    touched, ever. For the same reason a folder that is not empty and holds no
    manifest.txt - a checkout, Documents - is refused unless -Force.

    HOW AN UPDATE LANDS. Every new file is copied into a staging folder inside
    the installation first, so a failed copy changes nothing; then each one is
    moved into place, the old one renamed aside rather than overwritten, and a
    failure part-way puts back every file already moved.

    This file is read by Windows PowerShell 5.1 in the ANSI codepage: keep it
    ASCII.
#>
[CmdletBinding(DefaultParameterSetName = 'Install')]
param(
    [Parameter(ParameterSetName = 'Install')]
    [Parameter(ParameterSetName = 'Uninstall')]
    [string] $InstallDir = '',

    [Parameter(ParameterSetName = 'Install')] [switch] $NoShortcut,
    [Parameter(ParameterSetName = 'Install')] [switch] $Force,

    [Parameter(ParameterSetName = 'Package', Mandatory = $true)] [string] $Package,
    [Parameter(ParameterSetName = 'Package', Mandatory = $true)] [string] $Sha256,

    [Parameter(ParameterSetName = 'Install')]
    [Parameter(ParameterSetName = 'Package')] [int] $WaitPid = 0,
    [Parameter(ParameterSetName = 'Install')]
    [Parameter(ParameterSetName = 'Package')] [switch] $Launch,

    [Parameter(ParameterSetName = 'Uninstall', Mandatory = $true)] [switch] $Uninstall,

    [switch] $Quiet
)

$ErrorActionPreference = 'Stop'
$Self       = (Resolve-Path -LiteralPath $PSScriptRoot).Path
$DefaultDir = Join-Path $env:LOCALAPPDATA 'Programs\UpsTools'
$Shortcut   = Join-Path ([Environment]::GetFolderPath('Programs')) 'UPS Tools.lnk'
$ExeName    = 'UpsTools.exe'
$OldSuffix  = '.upstools-old'
$LogFile    = Join-Path $env:APPDATA 'UpsTools\ups-tools.log'
$ExitEvent  = 'Local\UpsTools.Exit'        # App.xaml.cs: what "UpsTools.exe --exit" sets
$RunKey     = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$RunValue   = 'UpsTools'                   # StartupRegistration.cs
$NotifyKey  = 'HKCU:\Software\Classes\AppUserModelId\DKozlov.UpsTools'   # Notifier.cs
$NotifyCache = Join-Path $env:LOCALAPPDATA 'UpsTools'                    # Notifier.CacheFolder
$TaskFolder = 'UpsTools'                   # ElevatedTasks.FolderPath, without the leading \

# ---- saying things ---------------------------------------------------------
# One line in the app's log, in AppLog's format, retried on a sharing violation.
function Log([string]$msg) {
    $line = '{0} [Install] {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $msg
    try { New-Item -ItemType Directory -Path (Split-Path -Parent $LogFile) -Force | Out-Null } catch { return }
    for ($i = 0; $i -lt 4; $i++) {
        try { [IO.File]::AppendAllText($LogFile, "$line`r`n", (New-Object Text.UTF8Encoding($false))); return }
        catch [IO.IOException] { Start-Sleep -Milliseconds 20 }
        catch { return }
    }
}

function Say([string]$msg) { Log $msg; Write-Host $msg }

# A refusal or a failure: logged, printed, and - under -Launch, when the console
# may be hidden - shown in a timed box. Then the app comes back, on whatever
# version is installed now, so asking for an update never leaves the user without one.
function Fail([string]$msg, [int]$code = 1) {
    Log "FAILED: $msg"
    Write-Host "install.ps1: $msg" -ForegroundColor Red
    if ($Launch -and -not $Quiet) {
        try { [void](New-Object -ComObject WScript.Shell).Popup("UPS Tools was not installed:`n`n$msg", 15, 'UPS Tools', 16) } catch {}
    }
    if (($Launch -or $script:WasRunning) -and $script:Target) { Wait-ForPid; Start-App $script:Target (-not $Launch) }
    exit $code
}

# ---- the app ---------------------------------------------------------------
# UpsTools.exe processes started from $dir.
function Get-AppProcesses([string]$dir) {
    return @(Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($ExeName)) -ErrorAction SilentlyContinue |
             Where-Object { $_.Path -and (Test-Inside (Full $_.Path) $dir) })
}

# Asks a copy running from $dir to exit, as "UpsTools.exe --exit" does, and waits.
# Returns whether one was running. Fails - before anything is changed - if it does not exit.
function Stop-App([string]$dir) {
    $running = Get-AppProcesses $dir
    if (-not $running) { return $false }
    Log "asking UPS Tools ($(($running | ForEach-Object { $_.Id }) -join ', ')) to exit"
    try {
        $signal = [Threading.EventWaitHandle]::OpenExisting($ExitEvent)
        [void]$signal.Set()
        $signal.Dispose()
    } catch {
        Log "could not signal it to exit ($($_.Exception.Message))"
    }
    foreach ($p in $running) {
        if (-not $p.WaitForExit(30000)) {
            Fail "UPS Tools (process $($p.Id)) is still running from $dir after 30s - exit it from the tray icon and try again" 1
        }
    }
    Log 'UPS Tools exited'
    return $true
}

function Start-App([string]$dir, [bool]$background) {
    $exe = Join-Path $dir $ExeName
    if (-not (Test-Path -LiteralPath $exe)) { Log "cannot start $exe - it is not there"; return }
    $other = @(Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($ExeName)) -ErrorAction SilentlyContinue |
               Where-Object { $_.Path -and -not (Test-Inside (Full $_.Path) $dir) })
    if ($other) {
        Say "another copy of UPS Tools is running ($($other[0].Path)); only one runs at a time, so this one will not start until it exits"
    }
    $a = @(); if ($background) { $a += '--background' }
    if ($a) { Start-Process -FilePath $exe -ArgumentList $a -WorkingDirectory $dir | Out-Null }
    else { Start-Process -FilePath $exe -WorkingDirectory $dir | Out-Null }
    Log "started $exe$(if ($background) { ' --background' })"
}

# The .NET Desktop Runtime the exe needs (it is framework-dependent).
function Test-DesktopRuntime() {
    $roots = @()
    if ($env:DOTNET_ROOT) { $roots += $env:DOTNET_ROOT }
    $roots += Join-Path $env:ProgramFiles 'dotnet'
    foreach ($r in $roots) {
        $shared = Join-Path $r 'shared\Microsoft.WindowsDesktop.App'
        if (Get-ChildItem -LiteralPath $shared -Directory -Filter '10.*' -ErrorAction SilentlyContinue) { return $true }
    }
    return $false
}

# ---- paths -----------------------------------------------------------------
function Full([string]$path) { return [IO.Path]::GetFullPath($path).TrimEnd('\') }

function Test-Inside([string]$inner, [string]$outer) {
    return ($inner -eq $outer) -or $inner.StartsWith("$outer\", [StringComparison]::OrdinalIgnoreCase)
}

# A manifest's lines, each a relative path that stays inside the folder. Anything
# else in one - rooted, "..", a drive - is refused rather than skipped: it would
# be a file this script deletes or overwrites outside the installation.
function Read-Manifest([string]$dir) {
    $file = Join-Path $dir 'manifest.txt'
    if (-not (Test-Path -LiteralPath $file)) { return $null }
    $out = @()
    foreach ($line in [IO.File]::ReadAllLines($file)) {
        $rel = $line.Trim() -replace '/', '\'
        if (-not $rel) { continue }
        if ([IO.Path]::IsPathRooted($rel) -or $rel -match '(^|\\)\.\.(\\|$)|:' -or $rel -ieq 'manifest.txt') {
            throw "$file lists '$line', which is not a file inside the package"
        }
        $out += $rel
    }
    return $out
}

function Wait-ForPid() {
    if (-not $WaitPid) { return }
    $p = Get-Process -Id $WaitPid -ErrorAction SilentlyContinue
    if (-not $p) { return }
    Log "waiting for process $WaitPid to exit"
    if (-not $p.WaitForExit(30000)) { Log "process $WaitPid is still running after 30s; going ahead" }
}

# Every empty folder under $root, deepest first; $root itself too when $andRoot.
function Remove-EmptyDirs([string]$root, [bool]$andRoot) {
    if (-not (Test-Path -LiteralPath $root)) { return }
    $dirs = @(Get-ChildItem -LiteralPath $root -Recurse -Directory -Force | Sort-Object { $_.FullName.Length } -Descending)
    foreach ($d in $dirs) {
        if (-not (Get-ChildItem -LiteralPath $d.FullName -Force)) { Remove-Item -LiteralPath $d.FullName -Force }
    }
    if ($andRoot -and -not (Get-ChildItem -LiteralPath $root -Force)) { Remove-Item -LiteralPath $root -Force }
}

# ============================================================================
# UNINSTALL
# ============================================================================
# "Run as administrator" scripts each have a task in \UpsTools that runs
# "UpsTools.exe --run-script ..." (ElevatedTasks.cs). The ones that start this
# installation are deleted, and the folder with them once it is empty. They were
# registered with the highest privileges, so deleting them takes administrator
# rights: one UAC prompt, and only when there is something to delete.
function Remove-AdminTasks([string]$target) {
    $service = New-Object -ComObject Schedule.Service
    $service.Connect()
    try { $folder = $service.GetFolder("\$TaskFolder") } catch { return }

    $all = @($folder.GetTasks(1))   # 1: hidden ones too
    $mine = @($all | Where-Object {
        $exec = @($_.Definition.Actions | Where-Object { $_.Type -eq 0 })   # 0: run a program
        $exec.Count -gt 0 -and (Test-Inside (Full $exec[0].Path) $target)
    } | ForEach-Object { $_.Name })
    if ($mine.Count -eq 0 -and $all.Count -gt 0) { return }   # only other installations' tasks

    $names = ($mine | ForEach-Object { "'" + ($_ -replace "'", "''") + "'" }) -join ','
    $script = @"
`$s = New-Object -ComObject Schedule.Service; `$s.Connect()
`$f = `$s.GetFolder('\$TaskFolder')
foreach (`$n in @($names)) { `$f.DeleteTask(`$n, 0) }
if (@(`$f.GetTasks(1)).Count -eq 0) { `$s.GetFolder('\').DeleteFolder('$TaskFolder', 0) }
"@
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($script))
    try {
        $p = Start-Process -FilePath (Join-Path $PSHOME 'powershell.exe') -Verb RunAs -WindowStyle Hidden -Wait -PassThru `
            -ArgumentList '-NoProfile', '-NonInteractive', '-EncodedCommand', $encoded
        if ($p.ExitCode -ne 0) { throw "exit code $($p.ExitCode)" }
        Say "deleted the administrator tasks of $($mine.Count) script(s) (\$TaskFolder in Task Scheduler)"
    } catch {
        Say "left the administrator tasks of $($mine.Count) script(s) in Task Scheduler (\$TaskFolder): $($_.Exception.Message)"
    }
}

function Invoke-Uninstall() {
    $target = Full $(if ($InstallDir) { $InstallDir } else { $DefaultDir })
    $files = Read-Manifest $target
    if ($null -eq $files) { Fail "$target is not an installation (it has no manifest.txt)" 2 }

    [void](Stop-App $target)

    foreach ($rel in @($files) + 'manifest.txt') {
        Remove-Item -LiteralPath (Join-Path $target $rel) -Force -ErrorAction SilentlyContinue
    }
    Get-ChildItem -LiteralPath $target -Recurse -Force -Filter "*$OldSuffix*" -ErrorAction SilentlyContinue |
        Remove-Item -Force -ErrorAction SilentlyContinue
    Remove-EmptyDirs $target $true

    if (Test-Path -LiteralPath $Shortcut) {
        $s = (New-Object -ComObject WScript.Shell).CreateShortcut($Shortcut)
        if (Test-Inside (Full $s.TargetPath) $target) { Remove-Item -LiteralPath $Shortcut -Force }
    }

    # Start with Windows: the app's own Run entry, when it starts this installation.
    $run = (Get-ItemProperty -Path $RunKey -Name $RunValue -ErrorAction SilentlyContinue).$RunValue
    if ($run -and $run.IndexOf("$target\$ExeName", [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        Remove-ItemProperty -Path $RunKey -Name $RunValue
        Log 'removed the Start with Windows entry'
    }

    # Notifications: the app's registration and the images its notifications show.
    # Both are re-created by the app on its next notification.
    if (Test-Path -LiteralPath $NotifyKey) {
        Remove-Item -LiteralPath $NotifyKey -Recurse -Force
        Log 'removed the notification registration'
    }
    if (Test-Path -LiteralPath $NotifyCache) {
        Get-ChildItem -LiteralPath $NotifyCache -File -Filter '*.png' | Where-Object { $_.Name -eq 'app.png' -or $_.Name -like 'logo-*.png' } |
            Remove-Item -Force -ErrorAction SilentlyContinue
        if (-not (Get-ChildItem -LiteralPath $NotifyCache -Force)) { Remove-Item -LiteralPath $NotifyCache -Force }
    }

    Remove-AdminTasks $target

    Say "uninstalled UPS Tools from $target"
    if (Test-Path -LiteralPath $target) {
        $left = @(Get-ChildItem -LiteralPath $target -Recurse -File -Force | ForEach-Object { $_.FullName.Substring($target.Length + 1) })
        Say "left in place - not installed by the installer: $($left -join ', ')"
    }
    Say "settings and the log are kept in $(Split-Path -Parent $LogFile)"
    exit 0
}

# ============================================================================
# UPDATE FROM A ZIP: verify, unzip, hand over to the new version's installer
# ============================================================================
function Invoke-Package() {
    $script:Target = $Self      # what Fail starts again: this installation, as it is
    Log "update of $Self from $Package requested"
    if ($null -eq (Read-Manifest $Self)) { Fail "$Self is not an installation (it has no manifest.txt)" 2 }
    if ($Sha256 -notmatch '^[0-9A-Fa-f]{64}$') { Fail "-Sha256 '$Sha256' is not a SHA-256" 2 }
    if (-not (Test-Path -LiteralPath $Package)) { Fail "there is no $Package" }

    # Copied before it is checked, and the COPY is what is checked and unzipped:
    # the download sits in a folder the user's own programs can write to.
    $work = Join-Path $env:TEMP ('UpsTools.Update-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $work | Out-Null
    try {
        $zip = Join-Path $work 'package.zip'
        Copy-Item -LiteralPath $Package -Destination $zip
        $got = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
        if ($got -ne $Sha256.ToUpperInvariant()) {
            Fail "the package's SHA-256 is $($got.ToLowerInvariant()), not $($Sha256.ToLowerInvariant())"
        }
        $pkg = Join-Path $work 'package'
        Expand-Archive -LiteralPath $zip -DestinationPath $pkg
        foreach ($f in 'install.ps1', 'manifest.txt', 'VERSION') {
            if (-not (Test-Path -LiteralPath (Join-Path $pkg $f))) { Fail "the package has no $f" }
        }

        # The NEW installer does the install: it is the one that knows the new layout.
        $ps = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
        $a = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$pkg\install.ps1`"",
               '-InstallDir', "`"$Self`"", '-NoShortcut', '-WaitPid', $WaitPid)
        if ($Launch) { $a += '-Launch' }
        if ($Quiet) { $a += '-Quiet' }
        Log "handing over to the package's install.ps1 ($((Get-Content -LiteralPath (Join-Path $pkg 'VERSION') -TotalCount 1).Trim()))"
        # Waited on as ONE process: Windows PowerShell's -Wait also waits for every
        # descendant, and the new installer's last act may be to start the app again.
        $p = Start-Process -FilePath $ps -ArgumentList $a -PassThru -NoNewWindow
        $null = $p.Handle                       # what makes ExitCode readable afterwards
        $p.WaitForExit()
        exit $p.ExitCode
    } catch {
        Fail $_.Exception.Message
    } finally {
        Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# ============================================================================
# INSTALL: this package into a folder
# ============================================================================
function Invoke-Install() {
    $target = Full $(if ($InstallDir) { $InstallDir } else { $DefaultDir })
    $old = $null
    if (Test-Path -LiteralPath $target) { $old = Read-Manifest $target }
    $script:Target = $(if ($null -ne $old) { $target })      # Fail starts again only what was there

    $files = Read-Manifest $Self
    if ($null -eq $files -or -not (Test-Path -LiteralPath (Join-Path $Self 'VERSION'))) {
        Fail "$Self is not a package (no manifest.txt or VERSION) - build one with package.ps1, or unzip a release" 2
    }
    foreach ($rel in $files) {
        if (-not (Test-Path -LiteralPath (Join-Path $Self $rel) -PathType Leaf)) { Fail "the package is missing $rel" 2 }
    }
    if ($files -notcontains $ExeName) { Fail "the package has no $ExeName" 2 }
    $version = (Get-Content -LiteralPath (Join-Path $Self 'VERSION') -TotalCount 1).Trim()
    if ((Test-Inside $target $Self) -or (Test-Inside $Self $target)) {
        Fail "cannot install into $target from $Self - one is inside the other" 2
    }
    if ($null -eq $old -and (Test-Path -LiteralPath $target) -and (Get-ChildItem -LiteralPath $target -Force) -and -not $Force) {
        Fail "$target is not empty and is not an installation (no manifest.txt) - pass -Force to install into it anyway" 2
    }
    $from = ''
    try { $from = (Get-Content -LiteralPath (Join-Path $target 'VERSION') -TotalCount 1 -ErrorAction Stop).Trim() } catch {}
    Log "installing $version into $target$(if ($from) { " over $from" })"

    Wait-ForPid
    $script:WasRunning = $false
    if (Test-Path -LiteralPath $target) { $script:WasRunning = Stop-App $target }
    New-Item -ItemType Directory -Path $target -Force | Out-Null

    # Left by an earlier install that could not delete them.
    Get-ChildItem -LiteralPath $target -Recurse -Force -Filter "*$OldSuffix*" -ErrorAction SilentlyContinue |
        Remove-Item -Force -ErrorAction SilentlyContinue

    # 1) Stage: every file copied in, before anything already there is touched.
    $stage = Join-Path $target ('.install-' + [Guid]::NewGuid().ToString('N'))
    $order = @($files) + 'manifest.txt'          # manifest.txt last: it says the install is whole
    try {
        foreach ($rel in $order) {
            $dst = Join-Path $stage $rel
            New-Item -ItemType Directory -Path (Split-Path -Parent $dst) -Force | Out-Null
            Copy-Item -LiteralPath (Join-Path $Self $rel) -Destination $dst
        }
    } catch {
        Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
        Fail "could not copy the package into $target ($($_.Exception.Message)) - nothing was changed"
    }

    # 2) Swap: each file moved into place, the old one renamed aside; undone on failure.
    $done = New-Object System.Collections.ArrayList
    try {
        foreach ($rel in $order) {
            $dst = Join-Path $target $rel
            New-Item -ItemType Directory -Path (Split-Path -Parent $dst) -Force | Out-Null
            $aside = $null
            if (Test-Path -LiteralPath $dst) {
                $aside = "$dst$OldSuffix-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
                [IO.File]::Move($dst, $aside)
            }
            [void]$done.Add(@{ dst = $dst; aside = $aside })
            [IO.File]::Move((Join-Path $stage $rel), $dst)
        }
    } catch {
        $why = $_.Exception.Message
        for ($i = $done.Count - 1; $i -ge 0; $i--) {
            $d = $done[$i]
            try {
                if (Test-Path -LiteralPath $d.dst) { Remove-Item -LiteralPath $d.dst -Force }
                if ($d.aside) { [IO.File]::Move($d.aside, $d.dst) }
            } catch { Log "rollback: could not put back $($d.dst): $($_.Exception.Message)" }
        }
        Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
        Fail "could not move the new files into place ($why) - the previous files were put back"
    }
    Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
    foreach ($d in $done) {
        if ($d.aside) { Remove-Item -LiteralPath $d.aside -Force -ErrorAction SilentlyContinue }
    }

    # 3) What the previous version shipped and this one does not.
    if ($old) {
        $now = @{}
        foreach ($rel in $files) { $now[$rel.ToLowerInvariant()] = $true }
        foreach ($rel in $old) {
            if ($now.ContainsKey($rel.ToLowerInvariant())) { continue }
            $f = Join-Path $target $rel
            if (Test-Path -LiteralPath $f -PathType Leaf) {
                Remove-Item -LiteralPath $f -Force -ErrorAction SilentlyContinue
                Log "removed $rel - no longer part of the package"
            }
        }
        Remove-EmptyDirs $target $false
    }

    # A package downloaded through a browser carries the mark of the web onto every
    # file unzipped from it; SmartScreen would ask about the exe on every start.
    foreach ($rel in $order) { Unblock-File -LiteralPath (Join-Path $target $rel) -ErrorAction SilentlyContinue }

    $script:Target = $target
    $exe = Join-Path $target $ExeName
    if (-not $NoShortcut) {
        $s = (New-Object -ComObject WScript.Shell).CreateShortcut($Shortcut)
        $s.TargetPath = $exe
        $s.Arguments = ''
        $s.WorkingDirectory = $target
        $s.IconLocation = "$exe,0"
        $s.Description = 'Monitor the UPS and run scripts on power events'
        $s.Save()
        Log "start menu shortcut: $Shortcut"
    }
    if (-not (Test-DesktopRuntime)) {
        Say 'WARNING: the .NET 10 Desktop Runtime (x64) is not installed - UPS Tools needs it (https://dotnet.microsoft.com/download/dotnet/10.0)'
    }

    Say "installed UPS Tools $version into $target$(if ($from) { " (was $from)" })"
    if ($Launch) { Start-App $target $false }
    elseif ($script:WasRunning) { Start-App $target $true }
    exit 0
}

$script:Target = $null
$script:WasRunning = $false
try {
    switch ($PSCmdlet.ParameterSetName) {
        'Uninstall' { Invoke-Uninstall }
        'Package'   { Invoke-Package }
        default     { Invoke-Install }
    }
} catch {
    Fail $_.Exception.Message
}
