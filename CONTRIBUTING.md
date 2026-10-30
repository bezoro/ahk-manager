# Contributing

Use English for code, comments, application text, documentation, commit messages, issues, and pull requests. Preserve user-authored script contents, names, paths, historical records, and compatibility identifiers in their original form.

Start with [ARCHITECTURE.md](ARCHITECTURE.md). Current product changes belong in `Core/`, `Backend/`, `UpdateHost/`, `PortableLauncher/`, and `Presentation.Electron/`. Archived WinUI/WPF projects do not validate the Electron interface.

## Environment

Clone the full Git history and tags. Run `git fetch --tags` if tags are missing. Local builds calculate their version from Git; when building without history, pass `-ReleaseVersion` explicitly to `tools/Build-Electron.ps1`.

Use Windows x64 and PowerShell 7 for packaging and native checks. CI pins Node.js 24.22.0 and the .NET SDK in `global.json`. Install dependencies from the existing lock files. AutoHotkey v2 is needed for interpreter discovery checks; CI prepares an official portable copy for read-only inspection.

Restore the pinned CSharpier formatter from the repository root:

```powershell
dotnet tool restore --configfile NuGet.Config
$work = Join-Path $env:TEMP ('ahk-manager-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
```

Commands below start at the repository root. Each test/build output must be an absolute directory that does not already exist, preserving evidence from earlier runs.

## When to run CI

Windows CI runs automatically when changes reach `main`, including a pull request merge. Pull request commits and branch pushes do not start the suite automatically.

For a substantial change that needs validation before merging, a maintainer applies the **ci:windows** label to the PR. This runs the complete Windows suite against that exact head and makes a successful same-repository build eligible for the public Test channel. The label does not start another run on later commits. Remove and reapply it when a new implementation milestone needs validation. Use the existing run's retry controls for a transient failure on the same commit.

Run local checks while iterating and group fixes before requesting another Windows run. Documentation-only follow-ups do not normally need the full suite. **Run workflow** remains available for an explicit validation run. Publication still requires the approved `main` push or trusted PR preview flow described in [UPDATES.md](UPDATES.md).

Apply **ci:hold** to cancel a PR validation that is blocked or no longer useful. That request starts no Windows runners. Unrelated labels neither start nor cancel the suite. Remove the hold label before deliberately requesting the next validation milestone.

## Frontend

```powershell
Push-Location Presentation.Electron
npm.cmd ci --ignore-scripts
npm.cmd run format:check
npm.cmd run architecture:check
npm.cmd run typecheck
npm.cmd test -- --out "$work/node-tests"
npm.cmd run build -- --out "$work/frontend"
Pop-Location
```

`npm.cmd run format` applies Prettier according to `.prettierrc.json` and `.prettierignore`. `.editorconfig` covers other source files. `architecture:check` validates layer dependencies and isolation from historical sources.

Node checks use adapters and temporary profiles. The Windows file-identity check is explicitly skipped on other operating systems. Three integration checks require snapshots from the .NET backend suite; CI supplies these inputs:

```powershell
$env:AHK_HEADER_SNAPSHOT_INPUT = "$work/backend/header-description-snapshot.json"
$env:AHK_FOREGROUND_ARGUMENTS_INPUT = "$work/backend/foreground-host-arguments.txt"
$env:AHK_STARTUP_RECOVERY_INPUT = "$work/backend/startup-recovery-snapshot.json"
```

For updater transport changes, also run the real Electron network checks used by Windows CI. A successful Node fetch test does not prove Chromium/Electron redirect behavior.

## Commits and versions

Use Conventional Commits with an optional scope:

```text
fix(startup): preserve the configured login preference
feat(updates): add a release history filter
refactor(core): separate process observation from catalog operations
build: update the Windows packaging tools
```

Accepted types are `feat`, `fix`, `perf`, `refactor`, `docs`, `ci`, `build`, `style`, `test`, `chore`, and `revert`. Explain decisions in the body when the subject is insufficient. Breaking changes use `!` or a `BREAKING CHANGE:` footer describing compatibility and migration.

CI checks only new PR/push commits. Stable versions follow Semantic Versioning: `feat` increments minor, breaking changes increment major, and other types increment patch. The highest required increment wins. In `Presentation.Electron`:

```powershell
npm.cmd run commits:check
npm.cmd run test:release
npm.cmd run release:identity
```

`release:identity` reads Git without changing tags. `node scripts/compute-release.cjs --json` exposes the same identity for tools. `package.json.version` is a development template and does not determine official versions.

Stable publication runs on `main` after Windows CI passes. Approved open PRs from this repository produce previews named `X.Y.Z-test.<run>.<attempt>.<PR>`. The publisher runs trusted code from `main`, rejects fork or stale-head builds, and never replaces existing release assets. PR builds do not receive signing or publication credentials. See [UPDATES.md](UPDATES.md) for channel selection, retries, signing, and older-client recovery.

## Backend and Core

```powershell
dotnet restore Tests.Backend/Tests.Backend.csproj --locked-mode --configfile NuGet.Config -r win-x64
dotnet publish Tests.Backend/Tests.Backend.csproj -c Release -r win-x64 --self-contained true --no-restore -o "$work/test-bin"
& "$work/test-bin/Tests.Backend.exe" --backend --root "$work/backend"
& "$work/test-bin/Tests.Backend.exe" --core --root "$work/core"
& "$work/test-bin/Tests.Backend.exe" --corrections --root "$work/corrections"
& "$work/test-bin/Tests.Backend.exe" --startup-inspection --root "$work/startup-inspection"
```

These selectors use temporary profiles and effect adapters. Startup inspection reads Windows WMI without changing registration. Core and correction checks also require a discoverable AutoHotkey v2 installation. Shared test adapters belong in `Tests.Backend/Fixtures`; Core cases belong in `Tests.Backend/Core`.

In `Presentation.Electron`, `npm.cmd run format:dotnet:check -- Core Backend Tests.Backend` checks CSharpier formatting; `format:dotnet` applies it. With no project list, both commands include UpdateHost. Formatting does not require project restore.

The separate `--ahk-owned` suite executes temporary scripts and requires additional setup. It is outside ordinary CI; read [SAFE-TESTS.md](SAFE-TESTS.md) before using it. Do not use a personal profile as a test root.

For update-host changes:

```powershell
dotnet restore UpdateHost/AhkManager.UpdateHost.csproj --locked-mode --configfile NuGet.Config -r win-x64
dotnet publish UpdateHost/AhkManager.UpdateHost.csproj -c Release -r win-x64 --self-contained true --no-restore -o "$work/update-host"
& "$work/update-host/AhkManager.UpdateHost.exe" --self-test --root "$work/update-host-tests"
```

## Windows interface and package

Use the production renderer bundle with the synthetic presentation host:

```powershell
Push-Location Presentation.Electron
node.exe node_modules/electron/install.js
Pop-Location
./tools/Test-ElectronPresentation.ps1 -FrontendRoot "$work/frontend" -HarnessRoot "$work/ui-harness" -OutputRoot "$work/ui-evidence"
```

The harness owns its window, exercises the renderer, and saves screenshots and assertions. It does not connect to the backend or run user scripts. It covers themes, narrow layouts, and zoom; it does not establish interactive UAC, real sign-in behavior, or Mica composition.

Build the complete distribution with fresh paths:

```powershell
./tools/Build-Electron.ps1 -OutputRoot "$work/build"
./tools/Package-Electron.ps1 -BuildRoot "$work/build" -OutputRoot "$work/package"
```

The build uses Electron's official runtime installer, checks types, runs Node tests, and publishes Backend and UpdateHost. Packaging creates NSIS and an exact owned-file inventory. `-DirectoryOnly` produces an unpacked app directory.

Portable packaging also requires the Visual Studio x64 C++ build tools and Windows SDK. Build its launcher with `tools/Build-PortableLauncher.ps1`, then pass the unpacked application directory and launcher to `tools/Build-PortablePackage.ps1`. Use `tools/New-PortableReleaseManifest.ps1` to generate the separate release manifest. All inputs must use the same release version and source commit. The ZIP contains `App`, a root launcher and package descriptors, never `Data`. See the Windows workflow for the exact build sequence.

`tools/Test-PortableLauncher.ps1` checks native argument forwarding and process behavior. `tools/Test-PortablePackage.ps1` checks archive inventory and release identity. `tools/Test-PortableUpdater.ps1` runs the real baseline/candidate update and rollback in a disposable Windows environment. Its separately built baseline is a test fixture and must not be published as a release asset. Changes to portable layout must keep the native helper, `RuntimeProfileLayout`, TypeScript deployment resolver, packaging script and their tests consistent.

`tools/Test-UpdateInstallation.ps1` installs, updates, and rolls back between real packages. Use a disposable Windows environment without a prior installation/profile, as described in [SAFE-TESTS.md](SAFE-TESTS.md). CI prepares that environment. A local package build never publishes a GitHub release.

## Preparing a change

Keep process/persistence rules in Core, protocol validation in Backend, native integration in Electron main, and presentation in the renderer. Preload methods must remain specific; do not expose arbitrary commands, module paths, or Node access.

When contracts change, review `Core/Models`, `Backend/Protocol.cs`, `Presentation.Electron/src/shared`, JSON contexts, and runtime validators together. Preserve existing profile, registry, and mutex identifiers. Format changes need prior-profile and invalid-data coverage.

Describe the reproduction, resulting behavior, and relevant validation in the PR. Inspect harness screenshots for visual changes. CI preserves logs and artifacts on failure. Historical native-test limits remain documented in [VALIDATION.md](VALIDATION.md).
