# Portable application updates

AHK Manager uses public releases from the fixed `bezoro/ahk-manager` repository. The application has one package type: a portable ZIP. Update checks and downloads need no GitHub account, personal access token or private-repository configuration.

## Channels and user actions

**Stable** is the default and receives releases published after successful Windows CI on `main`. **Test** also offers reviewed builds of open same-repository pull requests after their requested Windows CI passes. Forks, closed pull requests and superseded source commits are ineligible for preview publication.

The channel is saved independently of scripts and Windows-startup preferences. Switching it queries the feed without applying a version. Manager checks at startup, when the channel changes and every 15 minutes. Downloading and applying a package require an explicit user action.

In **Updates**, review the version and choose **Update and reopen** or **Install test version**. Save or discard pending edits and wait for current operations to finish. Manager closes its backend normally and reopens after application files change. Independent AHK processes keep running. Reopening uses `--foreground` and does not replay sign-in scripts.

Entering Test explicitly can offer a preview older than the current stable release. Remaining in Test after a stable update does not repeatedly recommend an older preview; it remains an explicit history choice. Returning to Stable offers its latest release even when that version is lower than the installed preview.

Version history combines compatible published portable releases with verified packages preserved locally. Rolling back changes application files, not profile or script contents. Stable does not offer cached previews. Releases without portable assets are not compatible rollback choices.

## Public transport and package integrity

The client reconstructs fixed GitHub API routes from validated asset identifiers. Renderer input and release-provided arbitrary URLs cannot select download locations. HTTPS origins, redirects, response sizes and request durations are bounded.

The Electron transport preserves the operating system's proxy and certificate configuration. Redirects are handled explicitly, with a bounded host allowlist. Network errors report a failed check rather than claiming that an outdated application is current. Polling and ETag caching limit anonymous API traffic; a token is not offered as a rate-limit workaround.

Portable release assets are:

- `AHK-Manager-<version>-Portable-x64.zip`
- `ahk-manager-portable-release.json`
- `ahk-manager-portable-release.json.p7s` when publisher signatures are required

The schema-1 manifest uses update protocol 2. It records the exact version, source commit, publication time, plain-text notes and an `archive` object containing the ZIP name, byte length, SHA256 and SHA512. Unexpected or duplicate fields, invalid versions and mismatched release/asset identities are refused. The ZIP downloader checks size and both hashes before caching; package proofs are checked again before application. The native helper has no network access.

## Version identity

Stable tags use `vX.Y.Z`. Conventional Commits select the highest increment since the latest reachable stable tag: `feat` increments minor, breaking changes increment major, and other accepted types increment patch. Breaking changes use `!` or a `BREAKING CHANGE:` footer. A tagged stable commit reuses its existing identity.

Previews use `X.Y.Z-test.<run>.<attempt>.<PR>`. Run and attempt order builds chronologically; the final identifier names the pull request. Calculation uses Git history and tags, excluding already-released changes shared with the PR head.

The Windows build calculates its own version, notes and source commit for the current attempt. A retry does not reuse an earlier job's identity accidentally. The launcher, application, package inventory, manifest and tag must identify the same full version and source.

## Trusted publication

Build jobs have read access. A separate job with `contents: write` publishes Stable only after all required Windows checks pass for a `main` push. Ordinary branch pushes and PR commits do not start Windows CI.

Preview publication runs from trusted tooling on `main` after an explicitly requested PR validation. It verifies the repository, successful run, current open PR head, attempt, artifact identity and ZIP digest. Only expected flat artifact files are extracted into a fresh bounded workspace. The publisher never executes package or script content supplied by the PR and does not expose signing or publication credentials to PR builds.

A successful main validation can discover eligible PR builds that completed before the preview publisher became available. Their original immutable build identity is retained.

Publication creates a draft, uploads and verifies the complete portable asset family, revalidates the tag/source identity, then publishes. Existing public tags and assets are never overwritten or extended. An incomplete draft resumes only when existing bytes match. A preview is marked as a prerelease and cannot replace GitHub's latest stable release.

## Publisher signatures

Production signing uses external GitHub secrets `WINDOWS_CSC_LINK` and `WINDOWS_CSC_KEY_PASSWORD`, plus public `WINDOWS_PUBLISHER_NAME`. Certificates and private keys never belong in the repository or an untrusted PR build.

When configured, the trusted publisher creates a detached CMS signature over the exact portable manifest bytes. This binds the archive digest without extracting or executing PR package content. The native verifier requires the configured publisher, trusted certificate chain, current validity, code-signing usage and SHA256. Manifest/signature identity remains pinned through publication retries.

A required missing or invalid signature blocks new downloads and cached rollback packages. Verification never falls back from required signatures to hashes alone. Without a configured certificate, releases have no verified publisher signature; hashes detect corruption but do not establish publisher identity.

## Applying a version

Portable plans use schema 2 and `deployment: portable`. The helper derives the root and selected data folder from the proven parent image under `App` and the bounded portable configuration. A plan cannot grant authority by supplying an unrelated path.

The helper is copied outside the replaceable application and receives a size-limited, hashed plan. It verifies the parent PID, creation time, user, session and image. Application requires a matching ready/commit exchange, drained operations, observed backend exit and normal Manager exit. Saving window placement is attempted; a crash or timeout does not prove shutdown.

The package inventory covers the full `App` payload, root launcher and package descriptors. Before replacement, the helper rechecks hashes, retained file identities, private ownership and access rules. It snapshots application files, stages the ZIP on the same volume and promotes the verified payload. `portable.json`, the selected data folder, source scripts and unrelated files are preserved.

A confirmed failure restores the previous application files. A timeout or unconfirmed result preserves evidence and blocks replay. Processes are not killed to force progress. After a durable success, only unchanged verified workspaces may be cleaned up; the full recovery backup and cached versions remain available.

Existing portable clients use `Updates/installers/<version>/archive.zip`, a protocol-2 receipt, the original `release.json` and any required signature. This historical cache directory name and compatible protocol field names remain so existing portable history is not lost. They do not enable executable installer packages.

Before an update, Manager preserves its current portable package from the public feed when available, making that version available for rollback. Plans, bounded logs, ready/commit documents, outcomes and snapshots live under `Updates/operations/<operationId>`. Do not delete unresolved evidence to retry an operation.

## Data and older portable versions

`Data` beside the root launcher is the default. **Preferences > Data location** prepares a private folder and applies the move on a normal restart. Profile and update history are copied and verified before the configuration pointer changes; Chromium storage is recreated. Source and recovery copies are retained. A pending data move blocks updating until completed or cancelled.

An older portable profile can retain its existing schema and script paths. One-time import of a verified previous Electron profile preserves its source. Retired distribution packages and unfinished operations are not carried into portable history.

For an update from 1.6.2 or earlier, open that older Manager as administrator first. Its deployed helper uses the caller token to reopen the new executable, which requires administrator access. Subsequent launches request elevation through Windows before initialization. See [PORTABLE.md](PORTABLE.md) for supported locations and recovery of older blocked portable folders.

## Validation

Node checks cover channel isolation, bounded public transport, integrity, persistence, preflights and offline recovery. Publication tests use mocked HTTP to exercise exact source identity, immutable assets and complete portable families. Native self-tests isolate storage policy and transactional recovery.

Windows CI tests the real Electron transport, captures the production renderer, verifies native manifests and child tokens, and builds the exact portable package. Separate disposable scenarios exercise profile import, actual update/rollback, default/custom data, relocation, shortcut repair and foreign-root refusal. Those checks do not observe a user's cloud account, interactive UAC prompt or next Windows sign-in. [SAFE-TESTS.md](SAFE-TESTS.md) and [VALIDATION.md](VALIDATION.md) define the evidence boundaries.
