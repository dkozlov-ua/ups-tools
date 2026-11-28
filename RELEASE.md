# Release runbook

UPS Tools ships as a setup, `UpsTools-X.Y.Z-setup.exe`, and an Android APK,
`UpsTools-X.Y.Z-android.apk`, on this repository's GitHub Releases. The setup installs per user
into one fixed folder that every later setup updates in place. There is no CI release:
releases are built and published from a local checkout. The setup isn't signed; the APK is,
with the app's own key ([Android](#android)).

## How it fits together

- **`VERSION`** is the only version (`X.Y.Z`, one line). `Directory.Build.props` reads it,
  and `package.ps1` passes it to the setup, so the exe, the setup and the tag can't disagree.
- **`installer\UpsTools.iss`** is the [Inno Setup 6](https://jrsoftware.org/isinfo.php)
  script. Its header has the details.
- **`package.ps1`** runs the test suite, builds the framework-dependent single-file
  `UpsTools.exe` with `dotnet publish`, and compiles it into
  `Releases\UpsTools-X.Y.Z-setup.exe` with Inno Setup. It builds from the working tree.
- **`package-android.ps1`** runs the Core and Android tests, and builds
  `Releases\UpsTools-X.Y.Z-android.apk`, signed with the app's key.
- **`publish.ps1`** checks that the release can go out, runs `package.ps1` and
  `package-android.ps1`, creates the release with both files with `gh release create` (which
  creates the tag on GitHub), and confirms that GitHub's SHA-256 of each asset is the file it
  built: the apps check their updates against it.

Building needs the .NET 10 SDK, the GitHub CLI (`gh`) and Inno Setup 6
(`winget install JRSoftware.InnoSetup`; a per-user install is enough), and for the APK the
android workload, a JDK, the Android SDK and the signing key (see the top of
`package-android.ps1`).

## What the setup does

- Installs into `%LOCALAPPDATA%\Programs\UpsTools` without administrator rights, with a
  *UPS Tools* Start menu shortcut and an entry in Settings → Apps → *Installed apps*.
- Warns, and offers the download page, when the .NET 10 Desktop Runtime (x64) is missing.
- A running UPS Tools holds its exe open, so the setup asks it to exit first
  (`UpsTools.exe --exit`, which disconnects from the UPS cleanly and waits up to 30 s),
  and starts it again in the tray afterwards.
- The folder stays the same across updates, so the shortcut, a taskbar pin and
  *Start with Windows* keep working. Settings, script mappings and the log are in
  `%APPDATA%\UpsTools`, which neither an update nor an uninstall touches.
- Uninstalling runs `UpsTools.exe --uninstall` before the files are deleted. That exits the
  app and removes what it created outside its folder: the *Start with Windows* entry, the
  notification registration and images (`%LOCALAPPDATA%\UpsTools`), and the scheduled tasks
  of scripts set to run as administrator (`\UpsTools`; asks for administrator rights once,
  only if there are any).

Inno Setup's standard switches work: `/SILENT` or `/VERYSILENT` for an unattended
install, `/DIR=<folder>` for another folder, `/LOG=<file>` for a log. The uninstaller,
`unins000.exe` in the installation folder, takes the same `/SILENT` switches.

## Cutting a release

1. Make sure `main` has everything to ship and the tests pass: `dotnet test`, from an elevated shell so the
   administrator-task tests run too. That includes the UI tests (`tests\UpsTools.UiTests`), which drive the app's
   windows for about a minute: leave the mouse and keyboard alone meanwhile. `package.ps1` runs the others again.
2. Pick `X.Y.Z` and write it into `VERSION`.
3. Write the notes at `docs\release-notes\vX.Y.Z.md` (see below).
4. Commit and push `main`.
5. Publish:
   ```powershell
   powershell -File publish.ps1
   ```
   It refuses, before building anything, on a dirty tree (untracked files included), a
   HEAD not on `origin/main`, a `VERSION` that disagrees, missing notes, an existing
   release or tag of that version, or an app whose *Project on GitHub* link points at
   another repository.
6. Update this PC: run `Releases\UpsTools-X.Y.Z-setup.exe` (or the one downloaded from the
   release).

To try a setup without publishing it, run `powershell -File package.ps1 -SkipTests` and
install it into a scratch folder: `UpsTools-X.Y.Z-setup.exe /DIR=<scratch folder>`. It
shares the *Installed apps* entry and the Start menu shortcut with the real installation,
so reinstall the real one afterwards.

### Release notes

Write them for someone deciding whether to update now: what changed for them and what
they need to do about it (usually nothing). Say plainly when a change affects the scripts
users attach to events (which events fire and when), or the Bluetooth connection.

## Android

The APK is signed with the app's own key, `~/.upstools-android/release.keystore`, whose
password is in `~/.upstools-android/keystore-password`. Both are outside this repository: chezmoi
syncs them from the private configs repo (`chezmoi update` on a new PC). The Release build signs
with them and fails without them.

**Never lose or replace the key.** Android installs an APK over the installed app only if it is
signed with the same key, settings kept. One signed with another key can't update it: the app
would have to be uninstalled first, which deletes its settings (paired UPSes, the sharing code).
The key's certificate has SHA-256
`2D:9C:79:39:4A:05:5E:12:16:66:FC:43:DA:07:50:BE:67:FC:64:30:FD:BE:4F:9E:D9:68:05:6C:80:8E:9E:18`;
`apksigner verify --print-certs <apk>` shows it.

On the phone, Settings → *Check for updates* installs the latest release's APK after checking it
against GitHub's SHA-256. To put a build on the phone without releasing it:
`powershell -File package-android.ps1 -Install`, over USB debugging or Wireless debugging (see
the top of the script). Release notes should say what changed on the phone too, when anything did.

## Installing for the first time

1. Install the [.NET 10 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/10.0).
   The setup warns if it is missing.
2. Download `UpsTools-X.Y.Z-setup.exe` from the release and run it. The setup isn't
   signed, so Windows SmartScreen may say *Windows protected your PC*: choose
   *More info* → *Run anyway*.

## Uninstalling

Settings → Apps → *Installed apps* → *UPS Tools* → *Uninstall*. The settings and the log
in `%APPDATA%\UpsTools` stay; delete that folder too to remove every trace.
