# Release runbook

UPS Tools ships as a zip on this repository's GitHub Releases, installed by its own
`install.ps1` into one fixed folder that every later update replaces in place. There is
no CI release and no signing: releases are built and published from a local checkout.

## How it fits together

- **`VERSION`** is the only version (`X.Y.Z`, one line). `Directory.Build.props` reads it,
  so the exe, the package and the tag can't disagree.
- **`package.ps1`** runs the test suite, then builds `Releases\UpsTools-X.Y.Z\` (the
  package, staged) and `Releases\UpsTools-X.Y.Z.zip` from the working tree: the
  framework-dependent single-file `UpsTools.exe` from `dotnet publish`, plus `install.ps1`.
  It adds `VERSION` and `manifest.txt` (every file in the package).
- **`publish.ps1`** checks that the release can go out, runs `package.ps1`, creates the
  release with `gh release create` (which creates the tag on GitHub), and confirms that
  GitHub's SHA-256 of the asset is the zip it built.
- **`install.ps1`** ships inside the package. Run from an unzipped package, it installs
  into `%LOCALAPPDATA%\Programs\UpsTools` (or `-InstallDir`) and adds a *UPS Tools* Start
  menu shortcut. `-Package <zip> -Sha256 <hex>`, run from an installation, is the update
  path, and `-Uninstall` removes an installation. Its header has the details.

A running UPS Tools holds its exe open, so the installer asks a copy running from the
target folder to exit (the `UpsTools.exe --exit` signal, which disconnects from the UPS
cleanly), installs, and starts it again in the tray.

## Cutting a release

1. Make sure `main` has everything to ship and the tests pass: `dotnet test`.
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
6. Update this PC from the release: download the zip, then
   `& "$env:LOCALAPPDATA\Programs\UpsTools\install.ps1" -Package <zip> -Sha256 <hex>`
   (the hash is on the release page), or unzip it and run its `install.ps1`.

To try a package without publishing it, run `powershell -File package.ps1` and then
`Releases\UpsTools-X.Y.Z\install.ps1 -InstallDir <scratch folder> -NoShortcut`.

### Release notes

Write them for someone deciding whether to update now: what changed for them and what
they need to do about it (usually nothing). Say plainly when a change affects the scripts
users attach to events (which events fire and when), or the Bluetooth connection.

## Installing for the first time

1. Install the [.NET 10 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/10.0).
   The installer warns if it is missing.
2. Download `UpsTools-X.Y.Z.zip` from the release and unzip it.
3. `powershell -ExecutionPolicy Bypass -File install.ps1 -Launch`

The folder is `%LOCALAPPDATA%\Programs\UpsTools` and stays that way, so the Start menu
shortcut, a taskbar pin and *Start with Windows* keep working across updates. Settings,
script mappings and the log are in `%APPDATA%\UpsTools`, separate from the installation,
so neither an update nor an uninstall touches them.

## Uninstalling

`& "$env:LOCALAPPDATA\Programs\UpsTools\install.ps1" -Uninstall` exits the app, deletes
the installed files, the shortcut, the *Start with Windows* entry, the notification
registration and images (`%LOCALAPPDATA%\UpsTools`) and the scheduled tasks of scripts set to
run as administrator (`\UpsTools`; asks for administrator rights once), and keeps
`%APPDATA%\UpsTools`.
