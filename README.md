# UPS Tools

<img src="tools/app-icon-preview.png" alt="App icon" height="64">

A small Windows tray app that watches a UPS / portable power station and runs your scripts when its state changes,
e.g. pause games and put the NAS to sleep when the grid goes down, hibernate the PC when the battery runs low, and wake
the NAS when the grid comes back.

- **Dashboard**: grid status, battery level, charge/discharge rate, estimated time on battery, input and output power, recent activity.
- **Scripts**: attach any number of scripts to each event. `.ahk`, `.bat`, `.cmd` and `.exe` files open with their Windows
  file association; `.ps1` files run hidden through PowerShell. Arguments are optional.
  - They start in the listed order; drag a script by its handle (or Alt+Up / Alt+Down) to reorder.
  - **Wait until it finishes**: the next script starts only after this one exits with code 0. If it fails (another exit
    code, or it can't be started), the scripts after it are skipped, and a notification says so.
  - **Run as administrator**: the script runs elevated without a UAC prompt when the event fires, so it works with
    nobody at the PC (see [Scripts that run as administrator](#scripts-that-run-as-administrator)).
  - **Add action** → *Sleep*, *Hibernate* or *Shut down*: no script needed. A window on top of everything counts down
    first (60 s by default, 0–600), with *Cancel* and *… now*; with nobody at the PC, it runs out and the action goes
    ahead. Cancelling skips the scripts after it. The scripts after *Sleep* or *Hibernate* run once the PC is back on.
    *Shut down* closes apps even if they have unsaved work, which the empty battery would lose anyway.
  - When the grid comes back, the *Grid lost* and *Battery low* scripts that haven't started yet are skipped, e.g.
    *Grid lost* → `wait 5 minutes` (waited for) → `hibernate` doesn't hibernate the PC after a 2-minute outage, and a
    countdown still running closes. When the UPS answers again, the same goes for the *UPS unreachable* scripts.
- **Script variables**: scripts get the event and the UPS's state in environment variables (see
  [Environment variables](#environment-variables)), so one script can serve several events or pass the state on.
- **Notifications**: a Windows notification for each event, e.g. *Grid lost — running on battery: Battery 87 % ·
  about 6 h 10 m left at 120 W*, naming the scripts it started. *Grid restored* says how long the outage lasted and
  replaces the *Grid lost* or *Battery low* notification; *UPS reachable again* replaces *UPS unreachable*. Clicking one
  opens the app.
- **Configuration**: pair the UPS, close to tray, start with Windows, UPS update period, how long the grid must be gone
  before *Grid lost*, the *Battery low* limits, which events notify.

| Event | Fires when |
|---|---|
| Grid lost | Mains voltage at the UPS input disappears (UPS switches to battery), and stays gone for the wait set in Configuration (none by default) |
| Battery low | On battery, the charge falls to 20 % or the time left to 10 minutes (both can be changed or turned off); once per outage |
| Grid restored | Mains voltage comes back |
| UPS unreachable | 3 polls in a row fail |
| UPS reachable again | The UPS answers again after being unreachable |

Events fire on transitions only. The first reading after start-up is a baseline, so starting the app during an outage
does not fire "Grid lost". *Battery low* is the exception: started on battery with the battery already low, it fires on
the first reading. The time left is the UPS's estimate at the current load, so it has to be at or below the limit in two
readings in a row: a short spike in load doesn't count.

With a wait before *Grid lost* (Configuration → Events), a blip shorter than the wait fires nothing: no scripts, no
notification, only a line in the log. Once the wait is over, *Grid lost* fires, and the outage (in *Grid restored* and
`UPS_OUTAGE_SECONDS`) counts from the first reading without the grid. *Battery low* waits for *Grid lost*.

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

Download `UpsTools-X.Y.Z-setup.exe` from [Releases](https://github.com/dkozlov-ua/ups-tools/releases) and run it. It
installs into `%LOCALAPPDATA%\Programs\UpsTools` without administrator rights, with a *UPS Tools* Start menu shortcut.
Running a newer setup updates that folder in place; uninstall it from Settings → Apps → *Installed apps*. The setup isn't
signed, so SmartScreen may ask you to confirm (*More info* → *Run anyway*). See [RELEASE.md](RELEASE.md).

## Usage

- Configuration → **Find UPS…** → select your UPS → **Pair**.
- Settings and the log are in `%APPDATA%\UpsTools` (`settings.json`, `ups-tools.log`).
- Notifications are registered as *UPS Tools* (`HKCU\Software\Classes\AppUserModelId\DKozlov.UpsTools`); their images
  are in `%LOCALAPPDATA%\UpsTools`. Windows' own settings (Do not disturb, the per-app switch) decide whether they pop up.
- `UpsTools.exe --background` starts hidden in the tray (used by "Start with Windows").
- `UpsTools.exe --run-script "<path>" [arguments]` runs a script and exits with its exit code (used by the administrator
  tasks).
- `UpsTools.exe --exit` makes a running copy exit cleanly and waits for it (up to 30 s; exit code 1 if it is still
  running), e.g. before replacing the exe.
- `UpsTools.exe --uninstall` exits a running copy and removes what the app created outside its folder (used by the
  uninstaller; see [RELEASE.md](RELEASE.md#what-the-setup-does)).
- Set the environment variable `UPSTOOLS_TRACE=1` to log the Bluetooth protocol exchange for troubleshooting.

### Environment variables

Every script gets these, with the UPS's state when the event fired (for *Run now*, its latest reading). One the UPS
doesn't report, or that doesn't apply, isn't set.

| Variable | Value |
|---|---|
| `UPS_EVENT` | `GridLost`, `BatteryLow`, `GridRestored`, `UpsUnreachable` or `UpsReachable` |
| `UPS_MANUAL` | `1` when started by *Run now* |
| `UPS_GRID` | `1` with mains power at the UPS input, `0` on battery |
| `UPS_REACHABLE` | `1` while the UPS answers, `0` when it is unreachable (the readings are then its last ones) |
| `UPS_BATTERY_PERCENT` | Charge, e.g. `87` |
| `UPS_RUNTIME_MINUTES` | Estimated minutes left on battery at the current load |
| `UPS_LOAD_W` | Output power in watts |
| `UPS_OUTAGE_SECONDS` | For *Grid restored*, how long the outage lasted; on battery, how long it has lasted so far |
| `UPS_MODEL`, `UPS_SERIAL` | The UPS |
| `UPS_READING_TIME` | When the readings were taken, e.g. `2026-11-05T21:30:05+02:00` |

E.g. in a `.ps1`: `if ([int]$env:UPS_BATTERY_PERCENT -lt 30) { ... }`; in a `.bat`: `echo %UPS_EVENT% >> ups.log`.

Scripts that run as administrator get them too: just before starting the task, UPS Tools writes them to
`%LOCALAPPDATA%\UpsTools\script-env`, where the task picks them up (a file older than 2 minutes is ignored).

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
- Uninstalling UPS Tools deletes the tasks; that asks for administrator rights once.

## Development

```
dotnet build
dotnet test tests/UpsTools.Tests        # protocol, monitor, events
dotnet test tests/UpsTools.App.Tests    # services, view models, the app's controller
dotnet test tests/UpsTools.UiTests      # end to end: drives the app's windows
dotnet run --project src/UpsTools
```

| Path | Contents |
|---|---|
| `src/UpsTools.Core` | Device-independent model (`UpsStatus`, `UpsMonitor`, `UpsStateTracker`, `IUpsDriver`), the Bluetti BLE driver and the simulated UPS |
| `src/UpsTools` | WPF app (Fluent theme, tray icon, settings, script runner) |
| `tests/UpsTools.Tests` | Core: the Bluetti protocol against reference vectors, a real Elite 100 V2 register dump and a simulated device (handshake, fragmented notifications, dropped links, stalls); the monitor, events, script chains, notification texts |
| `tests/UpsTools.App.Tests` | App: settings, log, Start with Windows, notifications, scripts (real processes), administrator tasks (real Task Scheduler), the uninstaller, tray art, view models, and the controller with a scripted UPS |
| `tests/UpsTools.UiTests` | End to end: starts `UpsTools.exe` with a simulated UPS and uses it through UI Automation and real mouse and keyboard input |
| `tools/make-icon.cs` | Regenerates `app.ico`: `dotnet run tools/make-icon.cs` |
| `VERSION` | The only version, `X.Y.Z` |
| `installer/UpsTools.iss` | The Inno Setup script for the setup |
| `package.ps1`, `publish.ps1` | Build the setup, publish a GitHub release: see [RELEASE.md](RELEASE.md) |

To support another manufacturer, implement `IUpsDriver` / `IUpsConnection` and register it in `UpsDrivers`.

### Testing

- **Profiles.** `UpsTools.exe --profile name` (or `UPSTOOLS_PROFILE=name`) runs a separate copy: its own
  settings and log (`%APPDATA%\UpsTools-name`), single-instance signals, Start with Windows entry, notification
  registration and administrator tasks (`\UpsTools-name`). It neither touches nor talks to the installed copy, which
  can keep running. The test suites run as such profiles and remove what they create.
- **Simulated UPS.** With `UPSTOOLS_SIMULATOR` set to a JSON file, the simulator is the only UPS driver. Every poll
  reads the file, e.g. `{"GridPresent": false, "BatteryPercent": 80, "AcOutputW": 120}`; `{"Fail": "message"}` makes
  polls fail (see `SimulatedUpsDriver`). Find UPS finds it while the file exists. With the simulator, *Sleep*,
  *Hibernate* and *Shut down* only write to the log.
- **Administrator tasks.** The tests that register real scheduled tasks run only in an elevated shell; otherwise
  they are skipped.
- **UI tests** start the app, click, type and drag, so they need the desktop to themselves while they run (about
  a minute): don't use the mouse or keyboard meanwhile. `package.ps1` doesn't run them.
