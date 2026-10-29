# Contributing

Use English for source identifiers, comments, documentation, commit messages, issues and pull request descriptions. The application interface uses English. Preserve user-provided script text, filenames, descriptions and profile data as entered.

## Set up

Use Windows 11 x64, the Node.js version pinned by CI and the .NET SDK pinned by `global.json`. Install dependencies from the lock files:

```powershell
cd Presentation.Electron
npm.cmd ci --ignore-scripts
npm.cmd run typecheck
npm.cmd run test:release
npm.cmd test -- --out C:\Temp\ahk-manager-node-tests
```

Use a fresh absolute output path for every run. Never run tests against a personal application profile, startup registration or operational AHK script. Dependency lifecycle hooks remain disabled; the build explicitly downloads the pinned Electron runtime with its official installer.

The repository's current layout is documented in [README.md](README.md). Shared services still linked from archived WinUI folders are active backend dependencies; do not assume that a folder name makes their contents unused.

## Choose validation that matches the change

Run typechecking and relevant Node tests for Electron changes. Use the Windows CI for actual process observation, native backend behavior, Electron networking and installer behavior. The presentation harness runs the production renderer with an isolated bridge and captures light/dark themes, narrow windows and large zoom levels.

For update changes, preserve the separate trust boundaries between PR builds, trusted publication, the renderer, the Electron host and the native applicator. Verify the actual Electron transport when changing networking; Node's Fetch behavior is not sufficient evidence for Electron's redirect behavior. See [UPDATES.md](UPDATES.md).

Keep serialized field names, enum values, startup fingerprints, registry paths, profile names and command-line flags compatible. Translating user-facing text does not authorize renaming persisted contracts or reinterpreting script syntax.

Process effects require current, verified identities and explicit consent where applicable. Do not add forced termination or automatic replay as a timeout fallback. Report uncertainty and preserve recovery evidence.

## Commits and versions

Use Conventional Commits, for example:

```text
fix(updates): handle public release asset redirects
feat(settings): add an independent startup preference
refactor(controller): separate selection policies
```

Use `!` or a `BREAKING CHANGE` footer only when the change is incompatible. Stable releases follow Semantic Versioning. Preview versions include the workflow run, attempt and PR identifiers and never reserve the next stable tag.

A pull request should explain the triggering problem, resulting behavior and relevant validation. Include screenshots for visible interface changes and describe any material limitations of the checks. Keep unrelated edits separate.

When a shared prerequisite PR and a follow-up PR depend on common commit ancestry, preserve that ancestry with a merge commit as documented in the PR. The release tooling relies on actual ancestry when excluding changes already released on `main`.

## Release credentials

The application is a public-release client and does not accept GitHub tokens. GitHub Actions uses its short-lived job token for trusted publication. Never add credentials to renderer state, source files, fixtures, logs, installers or examples. Code-signing credentials belong only in the trusted signing jobs described in [UPDATES.md](UPDATES.md).
