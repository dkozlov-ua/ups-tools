# Release runbook

UPS Tools ships as a setup, `UpsTools-X.Y.Z-setup.exe`, on this repository's GitHub Releases.
It installs per user into one fixed folder that every later setup updates in place. There is
no CI release and no signing: releases are built and published from a local checkout.

## How it fits together

- **`VERSION`** is the only version (`X.Y.Z`, one line). `Directory.Build.props` reads it,
  and `package.ps1` passes it to the setup, so the exe, the setup and the tag can't disagree.
- **`installer\UpsTools.iss`** is the [Inno Setup 6](https://jrsoftware.org/isinfo.php)
  script. Its header has the details.
- **`package.ps1`** runs the test suite, builds the framework-dependent single-file
  `UpsTools.exe` with `dotnet publish`, and compiles it into
  `Releases\UpsTools-X.Y.Z-setup.exe` with Inno Setup. It builds from the working tree.
- **`publish.ps1`** checks that the release can go out, runs `package.ps1`, creates the
  release with `gh release create` (which creates the tag on GitHub), and confirms that
  GitHub's SHA-256 of the asset is the setup it built.

Building needs the .NET 10 SDK, the GitHub CLI (`gh`) and Inno Setup 6
(`winget install JRSoftware.InnoSetup`; a per-user install is enough).

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

## Installing for the first time

1. Install the [.NET 10 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/10.0).
   The setup warns if it is missing.
2. Download `UpsTools-X.Y.Z-setup.exe` from the release and run it. The setup isn't
   signed, so Windows SmartScreen may say *Windows protected your PC*: choose
   *More info* → *Run anyway*.

## Uninstalling

Settings → Apps → *Installed apps* → *UPS Tools* → *Uninstall*. The settings and the log
in `%APPDATA%\UpsTools` stay; delete that folder too to remove every trace.
