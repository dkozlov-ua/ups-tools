# UPS Tools

<img src="tools/app-icon-preview.png" alt="App icon" height="64">

A small Windows tray app that watches one or more UPSes / portable power stations and runs your scripts when their
state changes, e.g. pause games and put the NAS to sleep when the grid goes down, hibernate the PC when the battery runs
low, and wake the NAS when the grid comes back.

- **Several UPSes**: pair as many as you like; they are monitored at the same time, each with its own scripts, event
  limits and notifications, and optionally a name of its own (e.g. *Desk*, *Server rack*) that notifications, the tray
  and the log use. With more than one, the dashboard shows all of them at once, and a picker next to the tabs chooses
  the UPS the scripts and the settings are about.
- **Network sharing**: several PCs can watch the same UPS. With *Share with other PCs* on and the same sharing code,
  the PCs on your network find each other, only one of them connects to each UPS over Bluetooth, and the others read
  it through that one, each with its own scripts and notifications; another PC takes over if that one goes away. UPSes
  another PC is connected to show up in **Add UPS…** (see [Network sharing](#network-sharing)).
- **Dashboard**: grid status, battery level, charge/discharge rate, estimated time on battery, input and output power, recent activity.
  With several UPSes, each has a compact panel of its own: six tiles in a row (two rows of three in a narrow window),
  each detail in a tooltip once it doesn't fit; past about two thirds of the window's height, they scroll.
- **History**: a chart of each UPS's charge and load over the last 6 hours, 24 hours or 7 days, with the outages
  shaded, the stretches without readings greyed and the events marked; pointing at it shows the reading there. Each
  PC keeps a point a minute for 7 days, also of a UPS it reads through another one.
- **Scripts**: attach any number of scripts to each event of each UPS. `.ahk`, `.bat`, `.cmd` and `.exe` files open with their Windows
  file association; `.ps1` files run hidden through PowerShell. Arguments are optional.
  - They start in the listed order; drag a script by its handle (or Alt+Up / Alt+Down) to reorder.
  - **Wait until it finishes**: the next script starts only after this one exits with code 0. If it fails (another exit
    code, or it can't be started), the scripts after it are skipped, and a notification says so.
    - **at most … min**: if it is still running after that long, the next script starts anyway and a notification says
      so; the script itself keeps running. 0 (the default) waits as long as it takes. Use it for a script that could
      hang before a *Hibernate*, e.g. one that shuts down VMs.
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
  about 6 h 10 m left at 120 W*, naming the scripts it started and the UPS. *Grid restored* says how long the outage
  lasted and replaces the same UPS's *Grid lost* or *Battery low* notification; *UPS reachable again* replaces
  *UPS unreachable*. Clicking one opens the app.
- **Tray icon**: coloured by the UPS that needs attention most (red: battery low or unreachable, yellow: on battery,
  green: all on the grid); its tooltip has a line for each UPS. After *Battery low*, the icon stays red, and the
  UPS's line says *Battery low*, until the grid is back. For each UPS, Configuration → Tray icon says:
  - **Show in the tooltip**: whether it has a line there. Windows cuts the tooltip at 127 characters, so with several
    UPSes, leave out the ones you don't need to see there; with none left, the tooltip is just *UPS Tools*.
  - **Grid lost**, **Battery low**, **UPS unreachable**: whether these events change the colour while they last. With
    one off, that UPS leaves the icon alone: on battery, it counts as on the grid; with its battery low, as on battery;
    unreachable, as its last reading says (grey without one). Its tooltip line, notifications and scripts stay as they
    are.
- **Configuration**: add and forget UPSes, close to tray, start with Windows, the update period; for each UPS, its
  name, how long the grid must be gone before *Grid lost*, the *Battery low* limits, which events notify, and how the
  tray icon shows it.
- **Updates**: Configuration → About → **Check for updates** looks for a newer release here; **Update to X.Y.Z** downloads
  its setup, checks it against the SHA-256 GitHub lists for it and runs it, which updates the app and opens it again.
- **Android**: an app for phones and tablets watches the same UPSes, with the dashboard, a notification for each
  event and network sharing with the PCs; it has no scripts (see [Android](#android)).

| Event | Fires when |
|---|---|
| Grid lost | Mains voltage at the UPS input disappears (UPS switches to battery), and stays gone for the *Grid lost grace period* set in Configuration (none by default) |
| Battery low | On battery, the charge falls to 20 % or the time left to 10 minutes (both can be changed or turned off), or, if set, the UPS has been unreachable on battery for a while; once per outage |
| Grid restored | Mains voltage comes back |
| UPS unreachable | 3 polls in a row fail |
| UPS reachable again | The UPS answers again after being unreachable |

Events fire on transitions only. The first reading after start-up is a baseline, so starting the app during an outage
does not fire "Grid lost". *Battery low* is the exception: started on battery with the battery already low, it fires on
the first reading. The time left is the stored energy divided by the average load of the last 5 minutes (the UPS's own
estimate isn't used: it runs far longer than the battery can last), and it has to be at or below the limit in two
readings in a row.

With a *Grid lost grace period* (Configuration → Events), a blip shorter than it fires nothing: no scripts, no
notification, only a line in the log. Once the grace period is over, *Grid lost* fires, and the outage (in *Grid
restored* and `UPS_OUTAGE_SECONDS`) counts from the first reading without the grid. *Battery low* waits for *Grid lost*.

If the UPS stops answering while on battery (e.g. its Bluetooth link drops), nothing tells how much charge is left.
With a *UPS unreachable on battery grace period* set (Configuration → Events, off by default), *Battery low* fires
once it has been unreachable that many minutes; its notification says so and shows the last reading, and its scripts get
`UPS_REACHABLE=0`. If the UPS answers again first, nothing fires; if it answers with the grid back, *Grid restored*
stops the *Battery low* scripts as usual.

## Supported devices

| Device | Transport | Status |
|---|---|---|
| Bluetti Elite 100 V2 (EL100V2) | Bluetooth LE (encrypted) | Tested |
| Bluetti AC60/AC60P, AC70/AC70P, AC180/AC180P/AC180T, AC2A/AC2P, AP300, EL10, EL30V2, PR30V2, PR100V2, Handsfree 1/2 | Bluetooth LE | Same register layout, untested |
| EcoFlow River 3 Plus, with or without its Smart Extra Battery | Bluetooth LE (encrypted, EcoFlow account) | Tested |
| EcoFlow River 3, River 3 UPS, River 3 Plus (270), River 3 Plus Wireless | Bluetooth LE (encrypted, EcoFlow account) | Same messages, untested |

Bluetti doesn't publish its protocol. The Bluetooth protocol and the "V2" channel encryption are ported from the community
reverse-engineering in [bluetti-bt-lib](https://github.com/Patrick762/bluetti-bt-lib) (MIT). Bluetti's official license
file is not needed.

Neither does EcoFlow. Its Bluetooth protocol (the secp160r1 key exchange, the session key table, the packet format and the
River 3 messages) is ported from [ha-ef-ble](https://github.com/rabits/ha-ef-ble) (Apache License 2.0). An EcoFlow device
only talks to the EcoFlow account it was added to in the EcoFlow app, so pairing one asks for that account's user ID: enter
it, or let Find UPS look it up by signing in to EcoFlow with the account's email and password. Only the user ID is saved
(in `settings.json`); the password is sent to EcoFlow (`api.ecoflow.com`) and nowhere else. The River 3 Plus doesn't
report how much energy it holds, so the time left is worked out from its model's capacity (286 Wh), plus 286 Wh for a
Smart Extra Battery when one is plugged in; it doesn't report its input voltage either, so the grid counts as present
while its AC input is plugged in.

A UPS accepts a single Bluetooth connection: while UPS Tools is connected to it, its phone app can only reach it over
Wi-Fi, and another PC can only reach it through this one ([Network sharing](#network-sharing)). UPS Tools lets go of it
when the PC sleeps, hibernates, shuts down or signs out, and when a laptop goes into Modern Standby (with or without
network in standby), and connects again once the PC is back. If Windows suspends UPS Tools without saying so, it
notices once it runs again and reconnects from scratch.

## Network sharing

Turn on Configuration → Network sharing → **Share with other PCs** on each PC that watches a UPS, and give them all
the same **sharing code** (the first PC makes one up: copy it, and paste it on the others). Then:

- The PCs find each other on the local network on their own: each announces itself every 5 s over UDP multicast
  (239.255.47.70, port 47870). The card lists the PCs found and the UPSes they are connected to, and a PC that has
  another code.
- For each UPS, only one PC connects over Bluetooth. The others read it through that PC over TCP (port 47870, or
  another free one), getting each reading as soon as that PC has it, and each runs its own scripts and notifications
  with its own event limits. The dashboard and the list of UPSes say which PC a UPS is read through.
- When the PCs start together, the one that connects is chosen by a fixed order. If it exits, sleeps (Modern Standby
  included), shuts down or turns sharing off, it tells the others, and one of them connects at once; if it disappears without a word (e.g. it
  lost its network), the others take over after about 17 s. A PC that is connected but has had the UPS unreachable for
  2 minutes (e.g. out of Bluetooth range) lets another one try.
- **Mains power first**: a laptop or a phone on its own battery leaves the UPS to a PC (or phone) on mains power, which
  the link costs nothing that matters. When they start together, the one on mains power connects; when a laptop
  connected to the UPS is unplugged, or a PC on mains power joins, the one on mains power takes over within seconds
  (without the readings failing, so no *UPS unreachable*). Being plugged in only counts after a minute, so a quick
  charge takes nothing over; a desktop, or a laptop powered by the UPS during an outage, counts as on mains power. Two
  on mains power, or two on battery, don't take the UPS from each other. One on mains power that couldn't reach the
  UPS once it had taken over (e.g. out of Bluetooth range) doesn't take it over again for an hour. The card lists the
  PCs on battery as such. With a PC running a version before 0.12.0 among those that have the UPS paired, the fixed
  order alone decides, as before.
- **Add UPS…** lists the UPSes the other PCs are connected to: pairing one takes its name from that PC and doesn't
  connect to it.

Only PCs with the same code can take part: the announcements are encrypted and authenticated (AES-GCM) with a key
derived from the code, and so are the links that carry the readings, so nobody else on the network can read the UPS or
pass off a reading as its own (e.g. a fake *Grid lost* that runs your shutdown scripts). The code itself never goes over
the network. Windows may ask once whether UPS Tools may use the network: allow it on private networks. A PC with
sharing off connects over Bluetooth regardless, so turn it on on every PC that has the same UPS paired.

## Android

The Android app (Android 8.0 or later) monitors UPSes as the Windows app does, without the scripts: the dashboard
(every UPS, with the same readings), a notification for each event (with the same texts, the same event limits and
the same choice of which events notify), and network sharing. Tested with an EcoFlow River 3 Plus on a Samsung
Galaxy S22 (Android 16).

- **Install**: download `UpsTools-X.Y.Z-android.apk` from [Releases](https://github.com/dkozlov-ua/ups-tools/releases)
  on the phone and open it (Android asks once to allow installing apps from that source), or build it and install it
  over USB or Wi-Fi debugging: `powershell -File package-android.ps1 -Install`. Opening the app asks for *Nearby
  devices* (Android 12 and later; *Location* before, which Android needs for Bluetooth scans) and for notifications
  (Android 13 and later).
- **Updates**: Settings → *Check for updates* looks for a newer release here; *Update to X.Y.Z* downloads its APK,
  checks it against the SHA-256 GitHub lists for it, and hands it to Android, which asks to confirm (and, the first
  time, to allow UPS Tools to install apps). The settings stay, and monitoring starts again by itself. Every APK,
  released or built with `package-android.ps1`, is signed with the same key, so each one updates the installed app;
  see [RELEASE.md](RELEASE.md#android).
- **Add UPS** scans for UPSes nearby and lists the ones other devices share, as on Windows; an EcoFlow UPS asks for
  its account's user ID, or the account's email and password to look it up.
- **Monitoring** runs in the background, with a notification that stays meanwhile (*Monitoring status*): a line for
  each UPS, coloured as the tray icon on Windows (and each UPS's settings choose the same way whether it has a line
  there and which events change the colour). It starts when the app opens, and again when the phone restarts
  (Settings → *Start when the phone starts*); *Stop monitoring* in that notification or in Settings stops it until
  the app is opened again.
- **With the screen off**: Settings → *Keep running with the screen off* (on by default) keeps the processor awake
  while monitoring, so that polls and events don't wait for the screen to turn on; it costs battery, so it suits a
  phone or tablet that stays plugged in. Allow UPS Tools to run unrestricted when Settings offers it: some phones
  stop apps in the background otherwise. If Android froze the app anyway, it notices once it runs again and
  reconnects from scratch.
- **Network sharing** works between phones and PCs alike: the same code, the same Wi-Fi network (the phone hears the
  announcements while it shares). The phone can be the one connected to a UPS, serving the PCs, or read it through a
  PC. The others call it by its name in Android's settings (*About phone → Device name*), or its model. Unplugged, it
  leaves the UPS to a device on mains power (see [Network sharing](#network-sharing)); plugged in (a charger, a PC's
  USB port, a wireless pad), it takes the UPS over from one on battery.
- **Notifications** come in three channels that Android's settings show: *Power problems* (Grid lost, Battery low,
  UPS unreachable: they pop up and sound), *Power back* (Grid restored, UPS reachable again) and *Monitoring status*.
- **History**: tapping a UPS on the dashboard opens its chart (6 h / 24 h / 7 d, as on Windows; touch it to read a
  point) and the events of that time. *Settings* there opens the UPS's settings.
- **Webhooks**: each UPS's settings can have URLs that its events call with a POST, as scripts are on Windows: pick
  the events, and what the request carries:
  - **JSON**, for Home Assistant, n8n, Node-RED: `event`, `title`, `message` (the notification's text), `ups`,
    `grid`, `reachable`, `batteryPercent`, `runtimeMinutes`, `loadW`, `outageSeconds`, `model`, `serial`,
    `readingTime` and `device` (the phone), as [the scripts' variables](#environment-variables); a value the UPS
    doesn't report is left out.
  - **Text**, for ntfy: the notification's text, with its title (and the UPS) in a `Title` header and `Priority:
    high` for the problems.

  A webhook that fails is tried twice more over half a minute, and the log says how it went; *Send test* sends a
  *Grid lost* marked `"test": true`.
- **Tasker, MacroDroid, Automate**: every event is also broadcast to the apps on the phone as
  `com.dkozlov.upstools.EVENT`, with the same fields as extras (and all of them as JSON in `json`).
- **At a glance**: a **Quick Settings tile** (the state, e.g. *On battery*, and the charge and load) and a
  **home-screen widget** (a line per UPS, with a dot in its colour), both following the status notification. Tapping
  either opens the app.
- Settings, the log and the history (`settings.json`, `ups-tools.log`, `history/`) are in the app's own storage; they
  aren't backed up. Settings → *Share log* sends the log (with the app's and the phone's versions) through Android's
  share sheet, e.g. to email it or save it to Drive.

## Requirements

- Windows 10 2004 or later (Windows 11 recommended), Bluetooth LE adapter
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (x64)

## Install

Download `UpsTools-X.Y.Z-setup.exe` from [Releases](https://github.com/dkozlov-ua/ups-tools/releases) and run it. It
installs into `%LOCALAPPDATA%\Programs\UpsTools` without administrator rights, with a *UPS Tools* Start menu shortcut.
Running a newer setup, or **Update** in the app, updates that folder in place; uninstall it from Settings → Apps → *Installed apps*. The setup isn't
signed, so SmartScreen may ask you to confirm (*More info* → *Run anyway*). See [RELEASE.md](RELEASE.md).

## Usage

- Configuration → **Add UPS…** → select your UPS → **Pair**. For an EcoFlow UPS, enter the user ID of its EcoFlow
  account first, or the account's email and password and **Look up user ID**. Do the same for each UPS; pairing one
  that is already in the list again updates it (e.g. its user ID) and keeps its scripts. **Forget** removes a UPS with
  its scripts.
- Settings, the log and the history are in `%APPDATA%\UpsTools` (`settings.json`, `ups-tools.log`, and
  `history\<id>.txt` for each UPS, a line a minute, kept 7 days). Settings of 0.6.0 and earlier
  (one UPS) are moved into the list of UPSes when the app starts; the old file stays as `settings.json.before-0.7.0`.
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
| `UPS_RUNTIME_MINUTES` | Estimated minutes left on battery at the average load of the last 5 minutes |
| `UPS_LOAD_W` | Output power in watts |
| `UPS_OUTAGE_SECONDS` | For *Grid restored*, how long the outage lasted; on battery, how long it has lasted so far |
| `UPS_NAME` | The UPS's name in UPS Tools: the one you gave it, or its brand and model, e.g. `Bluetti EL100V2` |
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
dotnet test tests/UpsTools.Android.Tests  # the Android app's controller, settings and advertisements, on Windows
powershell -File package-android.ps1      # the Android APK (needs the android workload, a JDK, the Android SDK and the key)
```

In a Debug build of the Android app, `files/simulator.json` in its storage makes the simulated UPS the only one, as
`UPSTOOLS_SIMULATOR` does on Windows: the emulator has no Bluetooth. Write it with
`adb shell "run-as com.dkozlov.upstools sh -c 'echo {\"GridPresent\": false} > files/simulator.json'"` and restart
the app; Release builds leave this out.

The Android app (`src/UpsTools.Android`) isn't in `UpsTools.slnx`, so building the Windows app doesn't need the
android workload; `dotnet workload install android` installs it, and `package-android.ps1` says how to get the SDK.
Release builds are trimmed and compiled ahead of time (about 16 MB), so the app's JSON goes through serializers
generated at build time (`JsonSerializerContext`), never reflection: Core is marked trimmable for `net10.0`, and the
trim analyzers fail the build on anything that wouldn't survive.
Core targets `net10.0` too, without the WinRT Bluetooth code (`Ble/Windows`): the Android app brings its own
(`IBlePlatform`), and the EcoFlow key exchange uses a managed secp160r1 there, as Android has only the NIST curves.

| Path | Contents |
|---|---|
| `src/UpsTools.Core` | Device-independent model (`UpsStatus`, `UpsMonitor`, `UpsStateTracker`, `IUpsDriver`), the shared Bluetooth LE link (`Ble`), the Bluetti and EcoFlow BLE drivers, the simulated UPS, and the history with its chart's layout (`History`), which each app draws with its own graphics |
| `src/UpsTools` | WPF app (Fluent theme, tray icon, settings, script runner) |
| `src/UpsTools.Android` | Android app: `Shared` (the controller, settings, advertisements; no Android in it) and `Platform` (Bluetooth LE, the foreground service, notifications, the screens). It uses the Windows app's dashboard texts and log as they are |
| `tests/UpsTools.Tests` | Core: the Bluetti protocol against reference vectors, a real Elite 100 V2 register dump and a simulated device (handshake, fragmented notifications, dropped links, stalls); the EcoFlow protocol against ha-ef-ble's algorithms, real River 3 packets and a simulated device (key exchange, authentication, uploads in parts, silence, reconnects); the monitor, events, script chains, notification texts |
| `tests/UpsTools.App.Tests` | App: settings, log, Start with Windows, notifications, scripts (real processes), administrator tasks (real Task Scheduler), the uninstaller, tray art, view models, and the controller with a scripted UPS |
| `tests/UpsTools.Android.Tests` | The Android app's `Shared` code, built for Windows: the controller with a scripted UPS, events and notifications, settings, two phones sharing a UPS, advertisements through the real drivers, updates from GitHub, webhooks |
| `tests/UpsTools.UiTests` | End to end: starts `UpsTools.exe` with a simulated UPS and uses it through UI Automation and real mouse and keyboard input |
| `tools/make-icon.cs` | Regenerates `app.ico`: `dotnet run tools/make-icon.cs` |
| `VERSION` | The only version, `X.Y.Z` |
| `installer/UpsTools.iss` | The Inno Setup script for the setup |
| `package.ps1`, `publish.ps1` | Build the setup, publish a GitHub release: see [RELEASE.md](RELEASE.md) |
| `package-android.ps1` | Build the Android APK, signed with the app's key (see [RELEASE.md](RELEASE.md#android)); `-Install` puts it on the phone |

To support another manufacturer, implement `IUpsDriver` / `IUpsConnection` and register it in `UpsDrivers`; a
Bluetooth LE driver takes the platform's `IBlePlatform`, so that it works on Android too.

### Testing

- **Profiles.** `UpsTools.exe --profile name` (or `UPSTOOLS_PROFILE=name`) runs a separate copy: its own
  settings and log (`%APPDATA%\UpsTools-name`), single-instance signals, Start with Windows entry, notification
  registration and administrator tasks (`\UpsTools-name`). It neither touches nor talks to the installed copy, which
  can keep running. The test suites run as such profiles and remove what they create.
- **Simulated UPS.** With `UPSTOOLS_SIMULATOR` set to a JSON file, the simulator is the only UPS driver. Every poll
  reads the file, e.g. `{"GridPresent": false, "BatteryPercent": 80, "AcOutputW": 120}`; `{"Fail": "message"}` makes
  polls fail (see `SimulatedUpsDriver`). Find UPS finds it while the file exists. Several files separated by `;` are
  several UPSes (`00:00:00:00:00:01`, `…:02`, …). With the simulator, *Sleep*,
  *Hibernate* and *Shut down* only write to the log.
- **Administrator tasks.** The tests that register real scheduled tasks run only in an elevated shell; otherwise
  they are skipped.
- **UI tests** start the app, click, type and drag, so they need the desktop to themselves while they run (about
  a minute): don't use the mouse or keyboard meanwhile. `package.ps1` doesn't run them.
