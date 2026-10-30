# Isolated Windows validation

The active product is Electron with a .NET backend. Ordinary tests use temporary profiles and adapters. Packaged integration tests run on disposable Windows runners, never against a personal profile or an existing installation.

## Windows CI

[Windows CI](.github/workflows/windows-ci.yml) runs on `windows-2025` automatically when changes reach `main`, including merges. A maintainer can request a PR run by applying **ci:windows**, or use manual dispatch for explicit validation. Ordinary PR commits and branch pushes do not trigger the suite. It uses the pinned .NET SDK, Node.js 24.22.0, and locked dependencies. Independent suites continue after other suites fail; any failure still fails the job.

Backend, Core, correction, and startup-inspection checks publish snapshots for the integrated Node checks. Missing backend snapshots do not turn integration tests into passing skips. The official AutoHotkey v2.0.30 portable download has a pinned SHA256 and is used for interpreter discovery and metadata inspection. Ordinary CI does not execute user AHK scripts or the separate `--ahk-owned` suite.

Backend tests use fresh roots and effect adapters. Startup inspection reads WMI without changing login registration. Node tests do not open windows, launch scripts, or write Windows startup settings. The real Electron transport suite separately tests Chromium redirect, download, integrity, and cache behavior.

The updater's native self-tests cover bounded protocol documents, package validation, identity checks, access control, transaction failures, and recovery. Test-only inert image adapters are confined to those tests; production updates verify the actual executable metadata and package contents.

`Tests.Backend.exe --startup-task-integration --root <new-absolute-fixture-directory>` performs the Windows Scheduler identity round trip. It registers disabled, inert tasks in a fresh GUID folder and tests SID/account-name combinations without running a task or touching Manager's production registration. Cleanup requires the acknowledged path, nonce and exact observed XML hash. Windows CI invokes it once and archives the task XML and check log. It validates registration; it does not simulate a Windows logon.

The native Windows self-test also creates a private, nonce-bound Cloud Files sync root when CFAPI is available. It exercises real hydrated placeholders, offline-file refusal, retained metadata/data handles and recovery snapshots without OneDrive credentials or a network provider. It authenticates the exact root, registration and child inventory before unregistering, since unregistration can delete dehydrated files. Unsupported platforms report an explicit skip; failures on a supported platform fail validation. This test does not claim to exercise OneDrive synchronization itself.

## Interface captures

`tools/Test-ElectronPresentation.ps1` opens the production renderer with a synthetic Electron host. It does not connect to the backend or execute scripts. It exercises light and dark themes, narrow layouts, 200% zoom, updates and rollback, preferences, and preparing or cancelling a custom data location. Screenshots and assertion results are saved in `windows-ui-evidence`.

These captures establish layout and control behavior with solid backgrounds. They do not establish Windows Mica composition, interactive UAC, or behavior at the next real sign-in. Inspect the captures when changing the interface.

## Installed integration tests

`tools/Test-UpdateInstallation.ps1` installs, updates, rolls back and uninstalls real packages in a temporary location. It requires no prior product registration or profile. It verifies registry ownership, hashes, version identities, and preservation of inert fixture files, then removes only its own confirmed registration and files. This package smoke test does not launch Manager or AHK scripts.

The separate installed-updater jobs launch the published preview or current candidate on a fresh runner and drive the actual app update action. Desktop automation binds to the fixture process identity and its owned dialogs. Positive cases use a registered private installation. Negative cases verify refusal for an unsafe parent or an unregistered executable. Fixtures explicitly disable automatic script launch and configure their own startup preferences.

## Portable integration tests

`tools/Test-PortableUpdater.ps1` uses a separately packaged older baseline and the current candidate. Both packages carry the reviewed source identity and their distinct complete versions. The baseline exists only for testing and is not a public release asset.

The driver launches the real portable application and checks profile import, update and rollback with default and custom data directories. It verifies that no Setup registration is required, a proven dedicated product root can become private beneath a permissive parent without changing the parent's permissions, and a root containing unrelated files is refused.

External fixture files, source profiles, startup state, and unrelated process identities are checked for preservation. A scoped helper may dismiss the exact known startup error dialog of its fixture process in a refusal case. It cannot acknowledge arbitrary desktop dialogs or operate on another process.

Portable signature tests create a short-lived CA and code-signing certificate inside the disposable runner. Only the public CA enters that elevated runner's local-machine root store; private keys remain in memory. Trust operations have deadlines and verify the fixture's exact certificate before cleanup. Signed fixture manifests and a valid CRL exercise the production native verifier with its normal publisher, digest, trust and revocation checks. The test retains the packaged publisher policy, seeds both cache signatures, and removes only its own certificate after the owned sessions close. A trust preflight runs before the separate real-app runners start. Fixture certificates and signatures never enter public release artifacts, and PR builds never receive production signing keys.

## Shutdown and cleanup

A shutdown request is not proof of exit. Updates require the matching operation/session receipt, observed backend exit, and normal Manager exit. A timeout is an unknown result and never authorizes force termination or replaying an operation. Delayed drain or placement completion cannot dispatch a late backend shutdown after cancellation.

Cleanup requests normal shutdown only for proven fixture processes. It never kills personal applications or scripts. Failures retain logs, receipts, outcomes and recovery copies. Evidence artifacts are retained for 14 days. Building a package locally does not publish a release.

The production application has no test switch to replace its profile, bypass effect ownership, disable the sandbox, or execute injected test code. Packaged tests use the normal layout, executable names, authenticated process ancestry and profile initialization.

## Additional native suites

`Tests.Backend --inventory-scoped-readonly --root <new-absolute-path>` reads a bounded process inventory once with a 30-second deadline. It writes only inert diagnostics and never starts, stops or deletes scripts. Its JSON contains fixed codes and counters rather than external script arguments.

The separate `--ahk-owned` suite executes harmless temporary scripts and requires its audited interpreter and fixture setup. It excludes a closed set of proven pre-existing process identities only while those identities and arguments remain unchanged. New or uncertain processes still prevent a safe conclusion. The native recycle step also requires the audited diagnostic Electron helper selected by `AHK_OWNED_TRASH_HELPER`, with a fixed path and hash. That helper is not distributed with the product.

Use a fresh absolute root for every attempt. Never use the personal profile or an old `publish` directory. Keep stdout, stderr, exit codes, assertions, captures and result documents.

WinUI/WPF projects and `tools/Run-SafeTests.ps1` are historical. Their selectors and screenshots do not validate the current Electron application. Historical UI or AHK tests still require isolated owned fixtures, no real startup registration, and normal process shutdown. See [VALIDATION.md](VALIDATION.md) for historical evidence.
