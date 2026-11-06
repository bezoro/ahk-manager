# Validation evidence

Validation applies to the portable product and the exact reviewed source commit. A successful unit test does not establish a Windows integration result, and a screenshot does not prove real process or update behavior.

## Evidence collected by CI

[Windows CI](.github/workflows/windows-ci.yml) runs after changes reach `main`. A maintainer can explicitly request a substantial PR validation; ordinary branch pushes and PR commits do not spend Windows runner time. See [CONTRIBUTING.md](CONTRIBUTING.md).

| Evidence                                                | What to verify                                                                                                                                                      |
| ------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `windows-ci-evidence`                                   | Locked restore, formatting, architecture, .NET and Node checks, native protocol/storage tests, backend snapshots, launcher manifest and actual child-token evidence |
| `windows-ui-evidence`                                   | Renderer assertion results, graceful fixture shutdown, and light/dark, narrow and zoom captures                                                                     |
| `windows-portable` or `windows-portable-test-<attempt>` | The exact versioned portable ZIP, protocol-2 manifest, hashes and optional signature                                                                                |
| Portable-updater scenario artifacts                     | Real application update and rollback outcomes, source identity, selected data paths, preservation and cleanup receipts                                              |

Report the workflow run, source commit, package version, assertion counts and any skips from that run's actual results. Preserve failing stdout, stderr, result documents and recovery records. Do not report expected counts as observed results or treat missing evidence as a passing skip.

Before publication, the complete portable asset family must identify one version and source commit. The released tag and artifact digests must match that identity. Public manifest downloads should be checked independently of the publisher's authenticated session. State whether the public binary bytes were downloaded and checked, or whether verification used CI package evidence and GitHub asset digests.

## Native scope

The Task Scheduler integration uses disabled tasks inside an isolated GUID folder. It verifies resolved account/SID identity, exact task shape, guarded updates and cleanup. It never runs a production login task or simulates a real Windows sign-in.

The launcher check extracts the embedded `requireAdministrator` manifest and verifies the actual child `TokenElevation`, arguments, executable, working directory and console policy. A read-only PE inspection requires the internal Electron executable to use `highestAvailable` with `uiAccess=false` and the exact release version. Native initializer checks reject an unelevated Manager or helper before profile changes. Packaged integration runners also require an elevated administrative backend. These checks prove elevated-run behavior; they do not prove an interactive medium-token UAC prompt was shown or accepted.

Cloud Files tests use a private, owned Windows CFAPI fixture where supported. They validate hydrated metadata, offline-file refusal, handle retention and guarded recovery. They do not exercise a user's OneDrive, Dropbox or Google Drive synchronization account.

Portable integration scenarios use disposable NTFS roots, synthetic profiles and inert external files. They exercise default/custom data paths, real updates and rollback, folder relocation, shortcut repair and refusal of unrelated root contents. User script files and personal profiles are outside those fixtures.

## Interface scope

The synthetic Electron presentation host uses the production renderer without connecting to the backend or executing scripts. Inspect the captured layouts when changing UI behavior. Captures establish controls, text and layout for the recorded sizes and themes; they do not establish Windows Mica composition or behavior at another DPI unless that case was captured.

## Reporting limits

State unresolved outcomes, failed cleanup, unsupported-platform skips and missing evidence explicitly. A timeout never proves exit or authorizes another application attempt. Distinguish verified startup configuration from a login actually observed on the user's computer.

Release notes and Git history preserve earlier work. Current validation reports should describe the current portable implementation instead of carrying obsolete setup instructions or archived interface results forward.
