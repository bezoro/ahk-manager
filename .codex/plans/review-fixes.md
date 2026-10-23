# Four review fixes

**READY:** planning is complete within the user's “Fix.” authorization. Implementation waits for the coordinator's fresh plan review. This describes proposed work, not completed fixes.

## Scope and evidence

Fix the four verified P2 findings from the project/frontend review. Preserve the existing product, protocol and workflow. No broad refactoring, dependency changes, commits, pushes, packaging/installation, real AutoHotkey execution, profile/startup writes, elevation or live application operations.

Coordinator-owned baseline: clean HEAD `4b8d0bdce00037e40448d3660a088d271becf841`; original source package `C:/Users/Bezor/.codex/review-evidence/ahk-manager-20261023/source-v1.zip`, SHA256 `7103a6069bf4320176bdf037f7d4d9248d754835d739e168402980e8c8639225`; index SHA256 `ef47f45d27abf8937ba9538913d66ec3e51a26496ea1161c60ebe37927db3830`. Its validated schema-1 manifest candidate is `sha256:1ab4c94600c4029380f8e4f89a5c20521a7065f978a3ce8d0ecdd749b1ea3286`. The coordinator owns change attribution, new evidence capture and binding validation.

Selective source inspection confirms:

- `AutoHotkeyDiscovery.ValidateScriptVersionAsync` parses only the third token as `System.Version`; both active manager/process launch paths call it. `Backend/AhkManager.Backend.csproj` links this WinUI-named source into the shipping backend.
- `scripts/test.cjs` launches diagnostics tests without the required fixture-root environment. The launcher already requires a fresh absolute `--out` directory.
- `App.tsx` keeps failed saves open but renders their feedback in the main column. Preferences owns its draft locally. Collection/import use the same completion path.
- The narrow CSS imposes two minimum grid-row heights while the main column's fixed content can consume the list's entire allocation.

Reuse existing failure evidence rather than rerunning unchanged review checks: `logs/requires-probe-v1.stdout.log` (SHA256 `d75432495ccd2242716c3187d2fa7dbf712ebeab190b856a096dd5621e0a7757`); standard frontend/isolated diagnostics logs; `frontend-review-fixture-v2/assertions-1.json` (`396462a34fff19b16003d29d0168e4d67090fd4fa9b415b928905c4038e8418a`) and `assertions-4.json` (`18a23dfbff7e7cd2e3d85164cf3c3efde370a142ba5dca185bb42445f069aa26`), with their existing screenshots. All are under the original evidence directory.

## Design and permitted writes

One implementation owner should sequence all source/test edits below. The coordinator owns combined UI execution; independent agents must not operate the same fixture window or modify shared files.

| ID | Exact write scope | Proposed change and dependency |
| --- | --- | --- |
| F1 | `Presentation.WinUI/Services/AutoHotkeyDiscovery.cs`; `Tests.WinUI/Core/DomainChecks.cs` | Replace the incompatible requirement parsing with a small cohesive evaluator; add deterministic text/metadata regressions in the existing linked core suite. |
| F2 | `Presentation.Electron/scripts/test.cjs`; existing `Presentation.Electron/tests/main/diagnostics.test.ts` only if a boundary assertion is needed | Create an isolated diagnostics directory beneath the validated `--out` root and pass it explicitly in the child environment. Preserve other environment values, fresh-directory protection and child exit/error propagation. |
| F3 | `Presentation.Electron/src/renderer/App.tsx`; `components/Preferences.tsx`; `components/Dialogs.tsx`; optional small `components/OperationNotice.tsx` | Give feedback an explicit initiating-dialog owner. Reuse the current message/detail presentation; share a small view/type only if this avoids duplication. Preserve local form state. Paths beginning `components/` are relative to `src/renderer/`. |
| F4 | `Presentation.Electron/src/renderer/styles.css` | At narrow/short sizes, allow the stacked workspace to scroll and reserve usable list content height rather than compressing the list to zero. Keep the current wide-screen composition. |
| T | `Presentation.Electron/tests/renderer/scenarios.ts`; `tests/main/presentation-harness.ts`; `tests/main/fixture-preload.ts` | Repair fixture GUIDs; permit 560-height fixture resizing and configurable fake method outcomes/rejections/import preview; add real-renderer regressions for F3/F4. Keep all changes test-only and avoid new production IPC. |

No other source/configuration files are authorized by this plan. New logs, builds, screenshots and fixture data belong beneath fresh children of `C:/Users/Bezor/.codex/review-evidence/ahk-manager-fix-20261023`, coordinated by the root. The fixture sources are tracked and included in the original source package; ordinary reviewed diffs deliver their changes. Do not change ignore rules or collect unrelated files.

### F1: compatibility contract

The authoritative [AutoHotkey requirement grammar](https://raw.githubusercontent.com/AutoHotkey/AutoHotkeyDocs/v2/docs/lib/_Requires.htm), inspected at lines 22–49, permits combined version comparisons, prereleases and executable bitness. Bare versions impose their documented major ceiling; explicit operators retain their own meaning. Compare missing components as zero, numeric and textual components correctly, prereleases below releases, and ignore `+` build suffixes. Every constraint and applicable directive must hold.

Use the selected interpreter's metadata for architecture, never the manager process bitness or its filename. Read PE headers without executing/loading the interpreter. Preserve the existing record's callers and display/version behavior; add only the metadata necessary for correct matching. Preserve the textual AHK version when evaluating prereleases: the official [resource definition](https://raw.githubusercontent.com/AutoHotkey/AutoHotkey/alpha/source/resources/AutoHotkey.rc) assigns `AHK_VERSION` to `ProductVersion` (lines 184–193). Reject invalid/unknown required metadata rather than guessing. Do not introduce runtime reflection.

Retain comment/continuation handling, supported decoding, cancellation, and the pre-launch incompatibility boundary. Recognize the actual directive token and legal trailing comments; this is not an expansion into full AHK parsing or include analysis. Public IPC/settings formats and existing launch/error contracts remain unchanged.

### F3/F4: user-visible contract

Keep failed preferences/collection/import dialogs open with their draft/preview intact. Show and announce their outcome/reason/technical detail within the active dialog; a background summary may remain. Scope thrown errors, picker failures and startup-transfer feedback to the same initiating preferences dialog. Capture the feedback destination when dispatching, rather than routing an unrelated completion to whichever dialog is currently open. Clear stale dialog feedback on a new attempt/open/close without remounting the form.

Only `Confirmed` closes a successful save/commit dialog. `Accepted`/`Pending` are acknowledgements/in-progress results; `Partial`, `Failed` and `Unknown` remain visibly distinct and must not claim completion or trigger automatic retries. Null picker results and `CancelledBeforeDispatch` retain the draft without presenting a save failure. Keep existing transport deadlines, busy guards, post-dispatch semantics and recovery mechanisms; add no timer, persistence protocol or automatic recovery flow.

At 760 × 560 with details open, a result notice and collapsed `PartialAnalysis`, the list must retain a usable row area or explicit reachable scroll access, with filters and inspector in separate non-overlapping regions. Expanded notices/details must remain reachable through bounded scrolling. Preserve list virtualization, keyboard navigation, detail toggling and `applyStylesToPortals={false}`.

## Execution, checks and acceptance

1. Coordinator obtains fresh plan review and resolves material findings. Existing red results establish the original four failures. If a new regression exercises a materially different cause, first establish that specific failure on unchanged production source using test-only changes. Pause only an inconclusive affected correction.
2. One implementer makes F1/F2 and F3/F4 with T, preserving unrelated edits. No test should need a real interpreter process or real profile. Use fresh explicit output roots.
3. Coordinator runs focused checks, then the affected integrated checks once: backend build and existing backend/core fake suites; frontend typecheck, normal `npm.cmd test -- --out <fresh-absolute-path>` with no caller-supplied diagnostics variable, frontend/harness builds and isolated real-renderer scenarios. Do not run `--ahk-owned`, install, package or live-operation suites. Reuse earlier unaffected checks with their stated limits.
4. Bind the final candidate and complete outputs through the existing schema-1 workflow helper. A distinct fresh implementation reviewer checks the four behaviors, safety boundaries and evidence. Resolve findings and rerun only invalidated checks before delivery.

Required regressions/observable completion:

- F1: with stable v2.0.30, accept `v2.0`, `>=2.0`, `v2.0-rc.2`, matching bitness-only and combined constraints; reject `v2.1`, bare `v1.1`, opposite bitness, unsatisfied upper/equality bounds and malformed requirements. Cover operator boundaries, multiple constraints/directives, zero-filled versions, release/prerelease ordering, build suffixes, comments/continuations and missing/malformed PE metadata using isolated inputs. Explicit `>=1.1 <3` must not inherit a bare-version major ceiling. Existing incompatibility safety checks must still pass.
- F2: the standard test command succeeds; diagnostics fixtures remain beneath its fresh output. A preexisting output is still rejected. Preserve evidence of the child process exit, not just an outer shell success.
- F3: real renderer tests prove visible semantic feedback inside each originating dialog, unchanged edited values after failure/rejection, sane cancellation/nonfinal outcomes, successful closure and no stale feedback after reopening. Inspect the active dialog's descendants/accessibility state, not the first global `.notice`. Include a pending attempt to verify duplicate dispatch stays blocked.
- F4: reproduce the exact minimum-size state and inspect actual list/row bounds, scrolling, selection and detail access. Test collapsed and expanded notices, details open/closed, and one existing wide light/dark layout. A document-overflow assertion alone does not establish usability. Capture the real renderer and review the relevant screenshots.

The fixture driver must use a fresh isolated userData/session/crash root, bounded existing command waits, durable assertion/error outputs and graceful close. A command acknowledgement alone is insufficient: require the expected assertion/capture artifact and clean fixture exit. On timeout or unknown dispatch state, inspect existing outputs; do not terminate unrelated processes or rerun effects blindly.

Completion is four corrected behaviors, passing relevant checks, review resolved and a reviewable diff/evidence package. Merge, release, packaging, installation and real-AHK evaluation are separate later gates outside this request. Runtime compatibility is validated here through text/metadata fixtures and existing fake boundaries; no real AHK execution is claimed.

## Risks and assumptions

- Version grammar and interpreter bitness are the main correctness risk; use the official rules and boundary fixtures, keeping the existing unsupported-interpreter refusal.
- Scrolling changes can affect virtualized list measurements/focus; verify behavior in the real renderer, not source/CSS snapshots alone.
- The original DOM scenarios were blocked by invalid fixture IDs. Their previous absence of assertions is not a pass; T must repair the fixture before relying on those scenarios.
- No user decision remains open. Minor implementation details may follow existing conventions within the listed files. Reopen planning only if expected behavior, architecture, material risk or acceptance changes.
