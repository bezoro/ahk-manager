# AHK Manager

AHK Manager runs and organizes AutoHotkey v2 scripts on Windows 11 x64. It combines an Electron, React and Fluent UI interface with a .NET backend that owns process identity, settings and recovery.

The application and active contributor documentation use English. Script contents, filenames, manually entered descriptions and existing profile data keep their original language.

## Manage scripts

- Add individual scripts or a folder without running any of them.
- Start, restart and request a normal stop for a selected script. Restart starts a replacement only after the previous process has exited.
- Choose each script's startup and administrator preferences independently.
- Edit with the configured editor. VS Code is detected by default; the Windows Edit association or a text editor is used when needed. Editing never uses the `.ahk` Open association that may execute a script.
- Inspect readable hotkeys, their original syntax, process observations and recorded activity.
- Recover missing script paths or remove catalog entries while preserving the files. Deleting a file requires confirmation and uses the Windows Recycle Bin.

The manager verifies identities again after native confirmations. Multiple instances, insufficient access, incomplete observations and uncertain operations remain visible instead of being treated as stopped. It never automatically kills a process to resolve uncertainty.

## Install and start

Use the Windows x64 installer from [GitHub Releases](https://github.com/bezoro/ahk-manager/releases). The installed package includes Electron and a self-contained .NET backend. Node.js, npm and the .NET SDK are development tools and are not required to run the application. Install AutoHotkey v2 separately to run scripts.

Running at Windows sign-in and administrator mode are enabled by default. Change them independently in **Preferences**. Elevated startup uses a scheduled task for the current user; standard startup uses a per-user sign-in entry. Windows may request approval when applying administrator settings. If registration cannot be completed, the application reports the pending setup.

Automatic sign-in startup does not display manager dialogs. Windows UAC can still request permission. The main window follows your **Preferences**, and startup status remains available in the interface.

Scripts marked for startup run at sign-in. A standard script stays under standard user privileges even when the manager is elevated. Reopening the manager, including after an application update, does not replay sign-in launches.

Closing the window can keep the manager in the notification area. **Exit and keep scripts running** leaves independent scripts running. **Stop scripts and exit** asks for consent, considers the complete catalog and exits only after the approved processes are confirmed stopped. Cancelling further stop requests does not restart scripts that have already stopped.

## Updates and test builds

Open **Updates** to check the installed version, select a channel and inspect version history.

- **Stable** is the default. It receives releases from `main` after Windows CI passes.
- **Test** includes previews from open pull requests in this repository after the same Windows checks pass.

Selecting a channel checks for releases. Downloading and installing always require an explicit button press. The application also checks at startup and every 15 minutes. Checks and downloads use public GitHub releases without a GitHub account or personal access token.

The selected channel survives restarts and updates. Returning to Stable offers the latest stable release even when it is older than the installed preview. Version history provides explicit rollback to compatible published or preserved installers. Updating or rolling back the application preserves the current profile and scripts; it does not undo script edits.

Application updates normally close and reopen the manager. Running scripts continue independently. Confirmed installation failures restore the preserved installation. An unknown result blocks another automatic application attempt so an uncertain effect is not repeated.

See [UPDATES.md](UPDATES.md) for release identity, network handling, integrity checks and recovery, including the one-time repair required by the redirect defect in versions 1.2.16 and 1.3.0.

## Data and Windows startup

Operational data is stored under `%LOCALAPPDATA%/Bezoro/AhkManagerElectron`. Chromium data is stored separately under `%APPDATA%/Bezoro/AhkManagerElectron/Chromium`. Existing profiles are not copied silently. Import and WinUI migration require selection, a preview and confirmation; migration preserves the source and creates an exact backup.

Application updates retain the installed executable path so verified Windows startup registrations continue to refer to the same installation. Before uninstalling an installation with an active administrator startup task, open the manager as administrator and disable its Windows startup preference. The uninstaller checks the task and refuses to remove an installation that still owns it.

Profiles, backups, update recovery records and scripts remain after uninstalling. The uninstaller verifies its owned-file inventory and preserves unrelated files and nonempty directories.

## Development

Use Windows, Node.js **24.22.0** and the .NET SDK pinned by `global.json`. Keep `package-lock.json` and the NuGet lock files intact. CI uses the same locked dependencies.

From `Presentation.Electron`:

```powershell
npm.cmd ci --ignore-scripts
npm.cmd run typecheck
npm.cmd run test:release
npm.cmd test -- --out C:\Temp\ahk-manager-node-tests
```

Use a new absolute output directory for every test or build. The scripts refuse existing staging directories.

Build and package from the repository root:

```powershell
./tools/Build-Electron.ps1 -OutputRoot C:\Temp\ahk-manager-build
./tools/Package-Electron.ps1 -BuildRoot C:\Temp\ahk-manager-build -OutputRoot C:\Temp\ahk-manager-package
```

Use `npm.cmd` in PowerShell. The build downloads the pinned Electron runtime through its official installer after `npm ci --ignore-scripts` has disabled arbitrary dependency lifecycle hooks.

The active solution contains Backend and Tests.Backend. The Electron application is under `Presentation.Electron`; the independent installer applicator is under `UpdateHost`. Some shared .NET services remain in the archived WinUI folders until the separate architectural refactor is merged. The archived UI projects are not part of the shipped Electron application.

Read [CONTRIBUTING.md](CONTRIBUTING.md) before changing process control, startup registration or the update protocol. Historical test and validation reports remain in [SAFE-TESTS.md](SAFE-TESTS.md) and [VALIDATION.md](VALIDATION.md), including older Portuguese records whose scope must not be mistaken for current validation.
