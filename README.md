# AHK Manager

A portable AutoHotkey v2 script manager for Windows 11 x64. Electron, React and Fluent UI provide the interface; a self-contained .NET backend owns process control, persistence and recovery.

[Download the latest portable release](https://github.com/bezoro/ahk-manager/releases/latest)

## Start using Manager

Extract `AHK-Manager-<version>-Portable-x64.zip` into its own local folder and open the root `Bezoro.AhkManager.exe`. Keep `portable.json`, `portable-package.json` and `App` together. The package includes Electron and .NET; AutoHotkey v2 is a separate requirement for executing scripts.

Manager always runs as administrator. Windows requests UAC approval when needed, before the launcher or application opens its files. Manager starts with Windows by default through a current-user scheduled task with highest privileges. This startup preference remains configurable. Each script keeps its own privilege preference; ordinary scripts still use the standard user token.

- Add scripts from anywhere on the system and choose which ones run at sign-in.
- Start, restart and stop scripts manually, with verified process identities.
- Edit scripts in automatically detected VS Code or configure another editor.
- Browse hotkeys, Activity, process observation problems and diagnostic logs.
- Update from GitHub, select Stable or Test, and return to a preserved earlier version.

Closing the window can leave Manager in the notification area. **Exit and keep scripts running** preserves independent scripts. **Stop scripts and exit** requests normal shutdown for verified processes and keeps Manager open if it cannot confirm the result. Adding or inspecting files does not execute them.

## Data, cloud folders and moving the app

By default, `Data` beside the launcher contains `Profile`, `Chromium`, `Updates` and `Logs`. **Preferences > Data location** can move it to another dedicated local folder. Script files remain at their original paths; they do not have to be inside `Data`.

Use local NTFS storage with all files available offline. Recognized Windows Cloud Files markers are supported without relaxing owner, permission or integrity checks. OneDrive and local mirrored folders can satisfy this policy; virtual streaming drives and unsupported redirection tags do not become supported merely because they belong to a sync provider. See [PORTABLE.md](PORTABLE.md) for cloud settings, migration and diagnostics.

Close Manager normally before moving its complete folder. Open the root launcher once from the new location to repair owned Start Menu/Search shortcuts and Windows startup paths. A relative `Data` location moves with it; an external absolute data path keeps its original meaning. Unrelated shortcuts, tasks and unproven startup entries are preserved.

## Updates

Manager checks public GitHub releases at startup and every 15 minutes. No GitHub account or token is required. **Stable** is the default. **Test** offers approved pull request builds before merge. Downloading and applying a version require an explicit action.

The updater verifies the portable ZIP, closes Manager normally, replaces application files and reopens it. Profile data and independent scripts remain in place. Confirmed failures restore the previous application; unknown results retain evidence and block automatic replay. [UPDATES.md](UPDATES.md) explains release identity, signing, history and recovery.

For the first update from version 1.6.2 or earlier, open that older Manager as administrator before updating. Its already-deployed helper needs an elevated token to reopen the new administrator-required application. Later launches enforce elevation through Windows.

## Contributing

Use English for code, comments, documentation, commits, issues and pull requests. Preserve user-authored names, paths, script contents and persisted compatibility identifiers.

| Path                     | Responsibility                                                               |
| ------------------------ | ---------------------------------------------------------------------------- |
| `Core/`                  | Contracts, catalog, persistence, process observation and Windows integration |
| `Backend/`               | Authenticated host session, command protocol and administrative coordination |
| `Presentation.Electron/` | Desktop integration, narrow preload API and React interface                  |
| `PortableLauncher/`      | Native root launcher with mandatory administrator manifest                   |
| `UpdateHost/`            | Verified portable update, rollback and recovery                              |
| `Tests.Backend/`         | Isolated backend and Core checks                                             |
| `tools/`                 | Build, portable distribution, publication and validation                     |

Read [CONTRIBUTING.md](CONTRIBUTING.md) for setup and commands, [ARCHITECTURE.md](ARCHITECTURE.md) for boundaries, and [SAFE-TESTS.md](SAFE-TESTS.md) for test isolation. Windows CI checks the portable product and preserves logs and interface captures. [VALIDATION.md](VALIDATION.md) describes the evidence and its limits.
