# Application updates

AHK Manager uses public releases from the fixed `bezoro/ahk-manager` repository. Application update checks and downloads do not use GitHub credentials. The application has no personal access token field or private-repository configuration.

## Channels and user actions

**Stable** is selected by default and receives releases published after successful Windows CI on `main`. **Test** also offers previews of open pull requests from this repository after their Windows CI passes. Fork builds, closed pull requests and superseded source commits are not eligible for preview publication.

The selected channel is saved independently of scripts and startup preferences. Switching channels queries the feed; it does not install anything. The application checks at startup, when the channel changes and every 15 minutes. A user must explicitly download and install a selected version.

Open **Updates**, review the release and select **Update and reopen** or **Install test version**. Save or discard pending edits and wait for existing operations to finish. The manager requests confirmation, shuts down the backend normally and reopens after the version changes. Independent AHK processes keep running. Reopening uses `--foreground` and does not replay sign-in script launches.

Entering Test explicitly may offer a preview older than the installed stable bootstrap release. After installing a stable release, remaining in Test does not repeatedly recommend an older preview; it remains an explicit history choice. Returning to Stable offers its latest release even when its version is lower than the installed preview.

Version history combines compatible published releases with verified packages preserved on this computer. Each edition offers its own package type. Rolling back changes application files, not the current profile or script contents. The Stable channel does not offer cached previews.

## Public network transport

The application reconstructs fixed GitHub API routes from validated asset identifiers. It does not accept download locations from renderer input or release-provided URLs. HTTPS origins, redirects, response sizes and request durations are bounded.

The Electron transport preserves the operating system's proxy and certificate configuration. Metadata and asset redirects are handled explicitly so a Chromium manual-redirect cancellation cannot be mistaken for an empty update feed. Network errors report a failed check; they never claim that an outdated installation is current.

GitHub may temporarily limit anonymous requests. The application retains bounded polling and ETag caching and reports when a check should be retried. A personal access token is not required or offered as a workaround.

Credential files written by earlier releases are retired. Cleanup never reads or decrypts their contents, never follows links, and never blocks public update checks when an old file cannot be removed safely.

## Version identity

Stable tags use `vX.Y.Z`. Conventional Commits determine the increment from the highest reachable stable tag: `feat` increments the minor version, breaking changes increment the major version, and the remaining accepted types increment the patch version. Breaking changes use `!` or a `BREAKING CHANGE` footer. Re-running a tagged stable commit reuses its existing version.

Previews use `X.Y.Z-test.<run>.<attempt>.<PR>`, for example `1.3.1-test.24.1.2`. Run and attempt identifiers order builds chronologically; the final identifier names the pull request. Preview calculation uses the latest stable tag and the common ancestor with the PR head, excluding changes that were already released through a shared commit.

The Windows build calculates its version, notes and source commit for its current attempt after installing locked tooling. It does not reuse an earlier successful job's identity when only a failed Windows job is retried. Each build has its own immutable tag and assets.

## Trusted publication

Build jobs have read access. A separate job with `contents: write` publishes a stable release only after all Windows checks pass for a `main` push.

Preview publication runs in a separate `workflow_run` workflow using release tooling checked out from `main`. It verifies the repository, successful CI, current open PR head, run attempt, artifact identity and ZIP digest. Only the expected flat files are extracted into a fresh directory with decompression bounds. The publisher does not run the installer or scripts supplied by the PR and does not expose publication credentials to the PR build.

A successful `main` CI also discovers eligible PR builds completed before the preview publisher became available. Their version identity remains the one calculated at build time. No manual download or rebuild is required for that backfill.

Publication first creates a draft, uploads and verifies the complete asset family, revalidates the tag and source, and only then publishes. Existing tags must identify the approved commit. Published tags and assets are never overwritten or extended. An incomplete draft can resume only when existing bytes match. A preview is marked as a prerelease and cannot replace GitHub's latest stable release.

Installed releases use `AHK-Manager-<version>-Setup-x64.exe` and `ahk-manager-release.json`. Update protocol 1 records the version, source commit, date, plain-text notes, installer name, size and SHA256/SHA512 hashes. The client rejects unexpected or duplicate manifest fields and mismatched release, asset or installer identity.

Releases that include the portable edition add `AHK-Manager-<version>-Portable-x64.zip` and `ahk-manager-portable-release.json`. Its schema-1 manifest uses update protocol 2 and an `archive` field with the ZIP name, size and both hashes. Both editions must identify the same version, source commit, publication date and notes. The legacy manifest retains its exact shape for older clients. The publisher accepts legacy installer-only artifacts or complete paired artifacts, never a partial family.

`electron-updater` owns installer download progress and SHA512 verification. A separate SHA256 check runs before caching and before the installer is handed to the applicator. Automatic download and install-on-quit are disabled. The native applicator does not access the network.

## Recovering builds with the earlier redirect defect

Versions **1.2.16**, **1.3.0** and **1.3.0-test.23.1.2** use an Electron Fetch path that cancels GitHub asset redirects when manual redirect handling is requested. Release metadata can be listed, but retrieving the release manifest fails. A successful Node Fetch test does not exercise this Electron behavior.

A failed check in these builds does not mean that no newer release exists. Adding a GitHub token does not repair the transport. The installed binary cannot download its own fix through the same broken manifest path.

The first upgrade requires the corrected **1.3.1** installer once:

1. Exit the old manager using **Exit and keep scripts running**. AHK scripts can remain active.
2. Run the corrected setup normally. It detects a registered per-user installation that supports update protocol 1 and offers a repair or upgrade confirmation. The default answer is No.
3. Confirm the repair. Setup validates the exact registered path, directory chain and installation ownership, then uses the existing guarded update path. Profiles, scripts and owned Windows startup settings stay in place.

The recovery path does not kill the manager, remove startup registration or ask the user to uninstall first. Unknown installations, older protocols, redirected paths and machine-wide conflicts remain rejected. A silent setup never repairs an existing installation implicitly; the explicit `--repair` maintenance flag is reserved for a caller that intentionally requests that operation, including the isolated Windows installer test.

After this one-time repair, later stable releases, previews and rollbacks use the application's update buttons. The fix is verified with the actual Electron runtime, including the legacy failure and bounded redirect handling in the corrected transport.

## Publisher signatures

To distribute signed installers, configure the GitHub secrets `WINDOWS_CSC_LINK` and `WINDOWS_CSC_KEY_PASSWORD`, plus the public variable `WINDOWS_PUBLISHER_NAME`. Certificates are external credentials and never belong in the repository.

The stable packaging job signs when configured. A trusted Windows preview publisher signs the downloaded installer and regenerates its manifest hashes without running the package. Unsigned PR builds retain the expected publisher policy in their application update configuration, so installing a preview does not silently remove future signature verification. The updater checks that publisher for both channels and for cached rollback installers.

For portable packages, the trusted publisher creates `ahk-manager-portable-release.json.p7s`, a detached CMS signature over the exact manifest bytes. It binds the archive checksum without extracting or executing PR content. The native verifier requires a trusted, currently valid code-signing certificate, SHA256, and an exact configured publisher name. The verified manifest and signature fingerprints remain pinned through publication retries and uploads. A required missing or invalid signature blocks both new downloads and cached rollback packages.

Without a configured certificate, releases have no verified publisher signature. Hash checks detect altered or corrupted bytes; they do not replace publisher signatures.

## Applying updates and recovering

NSIS accepts application-authorized updates only for a per-user installation with `UpdateProtocol=1`, a matching registered path and proven file identity. Versions through 1.1 require their documented manual migration because their uninstallers reject `--updated`.

The applicator is copied outside the installation and receives a size-limited, hashed plan. It verifies the parent process identifier, creation time, user, session and image. The manager permits application only after draining operations, attempting to save window placement and observing backend exit. The applicator requires the matching shutdown receipt and a zero manager exit code. A crash before that confirmation does not begin installation.

Before starting NSIS, the applicator verifies the installer again, snapshots installation files with hashes and captures the two owned registry trees. It compares the registered full version, numeric ProductVersion and complete FileVersion. Distinct previews and attempts are never treated as the same installed version merely because their numeric Windows version matches.

A confirmed installer failure or verified version mismatch preserves partial files and restores the previous installation and registry data. Cancellation or unconfirmed shutdown before application does not start the installer. A timeout never permits killing a process or replaying an unknown installation result. Recovery records remain available for investigation, and an unknown result blocks another automatic application attempt.

Installers and receipts are stored under `%LOCALAPPDATA%/Bezoro/AhkManagerElectron/Updates/installers/<version>`. Plans, bounded logs, results and snapshots are stored under `Updates/operations/<operationId>`. They survive uninstalling, along with profiles and scripts. Do not remove pending-operation evidence or version history during an installation.

## Preparation refusals

The installed edition requires the per-user installation record created by Manager Setup. Running its executable directly from a development `publish` directory or an unpacked installer build does not create that record. Run Setup once and open Manager from its installed shortcut to enable installed-edition updates. Alternatively, use the separate portable distribution described below. The profile and scripts remain outside the installed application directory.

The native helper also verifies that the installation directory and its parent have trusted ownership and permissions. A shared repository or custom directory writable by other Windows users may be refused. The updater does not loosen those permissions or change a repository's ACLs. Use the default private installation location or review the custom directory's permissions.

Each preparation attempt has its own directory under `Updates/operations`. A rejected attempt does not start the installer or publish a shutdown commit. The helper records the validation phase and a fixed error code; the app saves valid refusal metadata as `helper-refusal.json`. These records exclude exception messages, paths and stack traces. Older helpers can fail before creating `helper.log`, leaving only `plan.json` and `cancel.json`.

## Portable application and data

The portable ZIP contains a native root launcher, mutable `portable.json`, a complete `portable-package.json` file inventory, and the application under `App`. It contains no user data. The inventory binds the exact release version, source commit, byte sizes and SHA256 hashes. Duplicate, redirected, missing, extra or escaping archive entries are refused, with limits on entry count and compressed and expanded bytes.

The main process derives its deployment from the actual executable location. The initializer verifies a dedicated current-user-owned product container and prepares private access before Chromium starts. It may restrict permissions on that proven container; it never changes permissions on its containing repository or drive. Existing foreign files or an unsafe data directory cause a refusal. A private portable root can therefore live beneath a permissive repository parent without treating the parent as application-owned.

Portable plans use schema 2 with `deployment: portable`; their installation directory is the actual `App` directory. Package fields identify the verified ZIP. The helper derives the selected update store from the proven parent image and portable configuration before accepting a plan. It requires the same process identity, ready/commit receipts, confirmed backend shutdown and normal manager exit as an installed update. No Setup or uninstall registry entry is needed.

The helper stages the ZIP on the portable root's volume, verifies its complete contents, snapshots `App`, and promotes the new files. It also updates the root launcher and inventory. `portable.json` and the selected data directory remain outside application replacement. A confirmed failure restores the previous application files. An unknown result preserves staging, backups and evidence and blocks another apply; it never guesses which files to promote after a power interruption.

After recording a confirmed successful outcome, the helper checks the duplicate root workspaces against their snapshots, hashes and permissions before removing them. This keeps normal portable-folder relocation independent of old absolute recovery paths. Operation backups and cached rollback packages remain preserved. Foreign or changed workspace contents cause cleanup to be skipped, and failed or unknown operations retain their recovery files.

The selected data folder contains `Profile`, `Chromium`, and `Updates`. The default is `Data` beside the root launcher. **Preferences > Data location** prepares a move to a new or empty local folder and applies it on a normal restart. Operational data and update history are copied and verified before the configuration pointer changes. Chromium storage is recreated. The original data and recovery copies are retained. A pending data move blocks updating until it is completed or cancelled.

Portable cache entries use `Updates/installers/<version>/archive.zip` for compatibility with the common version-store layout, with a protocol-2 receipt, the original `release.json`, and a signature when required. Before a confirmed update, the current portable package is preserved from the public feed when available. This makes the first portable release rollbackable too. An installer-only historical release cannot be applied to a portable deployment. See [PORTABLE.md](PORTABLE.md) for migration and supported storage locations.

## Validation scope

Node tests cover channel isolation, persisted selection, public feed validation, bounded redirects, integrity checks, failure handling, serialization, preflights and offline cache recovery. Publication tests use mocked HTTP to verify source identity and immutability. Native applicator tests isolate protocol and recovery effects.

Windows CI runs the actual Electron network transport, renders the production interface, captures light/dark and narrow-window states, packages real installers, and exercises installation, updating, rollback and cleanup in temporary locations. These checks do not validate the user's interactive UAC prompt, personal Windows session or next real sign-in.

The installed-updater jobs launch the published preview and the current packaged candidate on fresh Windows runners, drive the real app's update action, and verify native readiness, confirmed shutdown, installer application and preservation. Separate cases verify refusal for a permissive custom parent and an unregistered executable. Candidate identity is checked against the CI manifest and source commit; scoped desktop automation controls only the exact fixture process.

The portable-updater jobs build a distinct baseline package and the candidate from the same reviewed source. They exercise the real packaged application, profile import, update and rollback with default and custom data locations. They also check dedicated-root preparation below a permissive parent and refusal for a container with unrelated files. Package metadata and receipts prove both versions; source profiles, external files, startup state and unrelated processes remain preserved.
