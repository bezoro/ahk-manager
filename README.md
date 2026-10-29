# AHK Manager

An AutoHotkey v2 script manager for Windows 11 x64. The desktop interface uses Electron, React, and Fluent UI. A .NET backend owns process control, persistence, and recovery.

[Download the latest stable release](https://github.com/bezoro/ahk-manager/releases/latest)

## Using the app

- Add scripts and choose which ones start when you sign in to Windows.
- Start, restart, and stop scripts with standard or administrator privileges.
- Edit scripts in automatically detected VS Code, or configure another editor and its arguments.
- Browse hotkeys, activity, and process observation problems.
- Update from the app, choose Stable or Test, and use version history to return to a previous release.

The manager starts with Windows and requests administrator access by default. These preferences are independent and configurable. Scripts without the administrator option continue to use the standard user token.

Automatic sign-in does not display Manager confirmation or warning dialogs. Windows UAC may still request elevation permission; the main window follows your visibility preferences. Startup issues remain available in the app.

Moving Manager to its installed location repairs an existing Manager login task only when its product marker, current user, action and logon trigger establish ownership. Other tasks and unproven Run entries remain untouched. Startup configuration problems appear in the interface and Activity without a blocking dialog when the app opens.

Closing the window can leave the app in the notification area. **Exit and keep scripts running** preserves script processes. **Stop scripts and exit** requests normal shutdown for verified processes and keeps the manager open if it cannot confirm the result.

Adding or inspecting files does not execute them. AutoHotkey v2 must be installed separately to run scripts. The installer includes Electron and the self-contained backend; users do not need Node.js or .NET.

## Updates and data

The app checks public GitHub releases at startup and every 15 minutes. No GitHub account, token, or advanced access setting is required. **Stable** is the default channel. Select **Test** on the Updates page to try approved pull request builds before they are merged. Downloads and installations require an explicit action.

Version history supports returning to an earlier installer. Profiles, scripts, and startup preferences are preserved. A confirmed installation failure restores the previous installation. See [UPDATES.md](UPDATES.md) for channel behavior, integrity checks, signing, and recovery of older builds that cannot check for updates.

Operational data is stored in `%LOCALAPPDATA%/Bezoro/AhkManagerElectron`. Chromium storage is separate, under `%APPDATA%/Bezoro/AhkManagerElectron/Chromium`. Migrating an older profile requires selection, a preview, and confirmation. Uninstalling preserves profiles and scripts.

## Contributing

Use English for source code, comments, documentation, commit messages, issues, and pull requests. User script names, paths, and existing data keep their original contents.

Read [CONTRIBUTING.md](CONTRIBUTING.md) for setup, formatting, tests, and packaging. [ARCHITECTURE.md](ARCHITECTURE.md) explains the dependencies and operational boundaries.

| Path                                  | Responsibility                                                                |
| ------------------------------------- | ----------------------------------------------------------------------------- |
| `Core/`                               | Contracts, catalog, persistence, process observation, and Windows integration |
| `Backend/`                            | Host session, command protocol, and administrative coordination               |
| `Presentation.Electron/src/main/`     | Application composition and desktop integration                               |
| `Presentation.Electron/src/preload/`  | Narrow interface between the host and renderer                                |
| `Presentation.Electron/src/renderer/` | Pages, components, and interface hooks                                        |
| `UpdateHost/`                         | Applying updates and restoring installations                                  |
| `Tests.Backend/`                      | Backend and Core tests with isolated adapters                                 |
| `tools/`                              | Builds, distribution, and isolated validation                                 |

Use Windows x64, PowerShell 7, Node.js 24, and the .NET SDK pinned in `global.json`. Dependencies are installed from the repository's lock files. From the repository root:

```powershell
$build = Join-Path $env:TEMP ('ahk-manager-' + [guid]::NewGuid().ToString('N'))
./tools/Build-Electron.ps1 -OutputRoot $build
./tools/Package-Electron.ps1 -BuildRoot $build -OutputRoot "${build}-package"
```

Windows CI checks formatting, architecture boundaries, types, .NET and Node tests, interface screenshots, and real installer update/rollback behavior. Artifacts contain the installer, logs, and screenshots. [SAFE-TESTS.md](SAFE-TESTS.md) describes test scope; [VALIDATION.md](VALIDATION.md) preserves historical validation evidence.

WinUI and WPF projects remain as historical implementations outside the active solution and distribution. Current shared code belongs in `Core/`. Earlier release notes are preserved in [docs/history.md](docs/history.md).
