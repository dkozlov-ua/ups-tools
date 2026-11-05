# UPS Tools

<img src="tools/app-icon-preview.png" alt="App icon" height="64">

A small Windows tray app that watches a UPS / portable power station and runs your scripts when its state changes,
e.g. pause games and put the NAS to sleep when the grid goes down, and wake the NAS when it comes back.

- **Dashboard**: grid status, battery level, charge/discharge rate, estimated time on battery, input and output power, recent activity.
- **Scripts**: attach any number of scripts to each event. `.ahk`, `.bat`, `.cmd` and `.exe` files open with their Windows
  file association; `.ps1` files run hidden through PowerShell. Arguments are optional.
  - They start in the listed order; drag a script by its handle (or Alt+Up / Alt+Down) to reorder.
  - **Wait until it finishes**: the next script starts only after this one exits with code 0. If it fails (another exit
    code, or it can't be started), the scripts after it are skipped, and a notification says so.
  - **Run as administrator**: the script runs elevated without a UAC prompt when the event fires, so it works with
    nobody at the PC (see [Scripts that run as administrator](#scripts-that-run-as-administrator)).
  - When the grid comes back or the UPS answers again, the opposite event's scripts that haven't started yet are skipped:
    e.g. *Grid lost* → `wait 5 minutes` (waited for) → `hibernate` doesn't hibernate the PC after a 2-minute outage.
- **Notifications**: a Windows notification for each event, e.g. *Grid lost — running on battery: Battery 87 % ·
  about 6 h 10 m left at 120 W*, naming the scripts it started. *Grid restored* says how long the outage lasted and
  replaces the *Grid lost* notification; *UPS reachable again* replaces *UPS unreachable*. Clicking one opens the app.
- **Configuration**: pair the UPS, close to tray, start with Windows, UPS update period, which events notify.

| Event | Fires when |
|---|---|
| Grid lost | Mains voltage at the UPS input disappears (UPS switches to battery) |
| Grid restored | Mains voltage comes back |
| UPS unreachable | 3 polls in a row fail |
| UPS reachable again | The UPS answers again after being unreachable |

Events fire on transitions only. The first reading after start-up is a baseline, so starting the app during an outage
does not fire "Grid lost".

## Supported devices

| Device | Transport | Status |
|---|---|---|
| Bluetti Elite 100 V2 (EL100V2) | Bluetooth LE (encrypted) | Tested |
| Bluetti AC60/AC60P, AC70/AC70P, AC180/AC180P/AC180T, AC2A/AC2P, AP300, EL10, EL30V2, PR30V2, PR100V2, Handsfree 1/2 | Bluetooth LE | Same register layout, untested |

Bluetti doesn't publish its protocol. The Bluetooth protocol and the "V2" channel encryption are ported from the community
reverse-engineering in [bluetti-bt-lib](https://github.com/Patrick762/bluetti-bt-lib) (MIT). Bluetti's official license
file is not needed.

The UPS accepts a single Bluetooth connection: while UPS Tools is connected, the Bluetti phone app can only reach the UPS
over Wi-Fi.

## Requirements

- Windows 10 2004 or later (Windows 11 recommended), Bluetooth LE adapter
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (x64)

## Install

Download `UpsTools-X.Y.Z.zip` from [Releases](https://github.com/dkozlov-ua/ups-tools/releases), unzip it, and run

```powershell
powershell -ExecutionPolicy Bypass -File install.ps1 -Launch
```

It installs into `%LOCALAPPDATA%\Programs\UpsTools` with a *UPS Tools* Start menu shortcut. Running a newer package's
`install.ps1` updates that folder in place; `install.ps1 -Uninstall` removes it. See [RELEASE.md](RELEASE.md).

## Usage

- Configuration → **Find UPS…** → select your UPS → **Pair**.
- Settings and the log are in `%APPDATA%\UpsTools` (`settings.json`, `ups-tools.log`).
- Notifications are registered as *UPS Tools* (`HKCU\Software\Classes\AppUserModelId\DKozlov.UpsTools`); their images
  are in `%LOCALAPPDATA%\UpsTools`. Windows' own settings (Do not disturb, the per-app switch) decide whether they pop up.
- `UpsTools.exe --background` starts hidden in the tray (used by "Start with Windows").
- `UpsTools.exe --run-script "<path>" [arguments]` runs a script and exits with its exit code (used by the administrator
  tasks).
- `UpsTools.exe --exit` makes a running copy exit cleanly, e.g. before replacing the exe.
- Set the environment variable `UPSTOOLS_TRACE=1` to log the Bluetooth protocol exchange for troubleshooting.

### Scripts that run as administrator

Ticking *Run as administrator* creates a scheduled task, `\UpsTools\script-<id>` in Task Scheduler, that runs
`UpsTools.exe --run-script "<script>" <arguments>` with the highest privileges. Creating it asks for administrator rights
once. When the event fires, UPS Tools starts the task, which needs no prompt, and reads the script's exit code from it.
The task runs on battery power too.

- Changing the arguments updates the task, which asks for administrator rights again. Unticking the box or removing the
  script deletes the task. If the task is missing or out of date (e.g. after restoring `settings.json` on another PC),
  the script shows **Repair**.
- Your Windows account must be an administrator. With a standard account, the task runs without elevation.
- Anything running under your account can edit a script set to run as administrator, or UPS Tools itself, and have
  it run elevated without a prompt. Keep those scripts in a folder only administrators can write to,
  e.g. under `C:\Program Files`, if that matters to you.
- `install.ps1 -Uninstall` deletes the tasks; that asks for administrator rights once.

## Development

```
dotnet build
dotnet test
dotnet run --project src/UpsTools
```

| Path | Contents |
|---|---|
| `src/UpsTools.Core` | Device-independent model (`UpsStatus`, `UpsMonitor`, `UpsStateTracker`, `IUpsDriver`) and the Bluetti BLE driver |
| `src/UpsTools` | WPF app (Fluent theme, tray icon, settings, script runner) |
| `tests/UpsTools.Tests` | Protocol tests against reference vectors and a real Elite 100 V2 register dump |
| `tools/make-icon.cs` | Regenerates `app.ico`: `dotnet run tools/make-icon.cs` |
| `VERSION` | The only version, `X.Y.Z` |
| `package.ps1`, `publish.ps1`, `install.ps1` | Release package, GitHub release, installer: see [RELEASE.md](RELEASE.md) |

To support another manufacturer, implement `IUpsDriver` / `IUpsConnection` and register it in `UpsDrivers`.
