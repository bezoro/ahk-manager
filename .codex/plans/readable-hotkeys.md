# Readable AutoHotkey hotkeys — release 1.0.4

**READY:** scoped for independent plan review. Implementation, runtime checks, external application, packaging and publication remain separate gates. This plan describes proposed work.

## 1. Scope and evidence

Automatically translate AHK v2 declarations into usable pt-BR instructions throughout the active Electron interface. Preserve F13, raw declarations, conflict identity, origin, context and partial-analysis reporting. There must be no filename-, script-name- or shortcut-specific substitution table.

Baseline: main at `01dfe3e1e25c1ade39d6b2df116ae2f623c72749`, clean according to root-owned `baseline-v1` (SHA-256 `a3c765c81a53ce077d2300cc09519d815e27c9648098fa7da054e854b12e9403`). Root owns subsequent status/change attribution. Versioned source IDs, paths and SHA-256 values are in `hotkeys-evidence-v4.json`; private source and evidence stay outside Git.

Decisive source locations:

- `Presentation.Electron/src/renderer/components/ScriptList.tsx:37`, `Inspector.tsx:31`, `Hotkeys.tsx:9–10`: active presentation and hotkey search currently use raw chord strings.
- `Presentation.Electron/src/shared/contracts.ts:15` and `Presentation.WinUI/Models/ManagerContracts.cs:47`: raw chord and normalized conflict identity are separate fields.
- `Presentation.WinUI/Services/HotkeyAnalyzer.cs:39–75,133–180`: bounded static reading, comment/string filtering, restrictive chord validation and normalization. These services are explicitly linked by `Backend/AhkManager.Backend.csproj`; archived WinUI/WPF interfaces are outside the implementation target.
- `Presentation.Electron/src/shared/validation.ts:14–17,27`: the active hotkey shape rejects unknown fields, so adding a wire property requires its explicit optional-field validator as part of integration.
- `private-desktop-original-v1`: authorized original, 7,193 bytes, SHA-256 `33047fee4c1b749e742ddb9a786775fdd6bba37460c0c4de45d7200aff5cebf9`. Selective verification found a release declaration calling a helper, a 320-ms configuration value, and state/timing logic that gates both clicks on empty desktop space. Its body and operational path must never enter Git.

The [official hotkey documentation](https://github.com/AutoHotkey/AutoHotkeyDocs/blob/v2/docs/Hotkeys.htm#symbols) establishes modifier/prefix semantics and that `Up` denotes release; it does not encode double-click. The [official key list](https://github.com/AutoHotkey/AutoHotkeyDocs/blob/v2/docs/KeyList.htm) covers mouse buttons, punctuation, F1–F24 and VK/SC identifiers. LButton/RButton are primary/secondary buttons; physical sides depend on mouse configuration.

Non-goals: arbitrary AHK interpretation, dynamic-hotkey evaluation, complete global-shortcut discovery, conflict-algorithm redesign, archived UI work, changing unrelated/private scripts or reloading operational processes.

## 2. Design and contracts

Use one pure renderer formatter, preferably `renderer/hotkeys.ts`, with localized vocabulary in the existing `resources/pt-BR.ts`. Return readable label, accessible instruction and explanatory detail from the unchanged snapshot. Use a small token parser, not chained global replacements or a new parsing framework. Format modifiers consistently Ctrl + Alt + Shift + Win; retain left/right distinctions. Parse terminal keys before treating every symbol as a prefix.

Coverage must include ordinary keys, literal punctuation, named key aliases, function keys, mouse buttons/wheel, release, custom two-key combinations, wildcard and pass-through. Explain technical hook prefix behavior in details. For `&`, describe holding the first key and pressing/releasing the second. A standalone Up arrow is distinct from an ` Up` release suffix. Preserve unknown tokens, VK/SC codes and unsupported combinations visibly; never invent a physical key or remove a modifier. Existing manual textual chords remain supported without rewriting their stored values.

Examples: `!^#+u` becomes “Ctrl + Alt + Shift + Win + U”; F13 remains “F13”; an unannotated mouse `Up` becomes a release instruction. Conventional mouse-side labels may say esquerdo/direito, with primary/secondary assignment explained in details. Do not infer a double-click from a release declaration.

Declare higher-level gestures through a small adjacent comment contract, consistent with the existing description marker:

```ahk
; @gesture double-click
~LButton Up:: ExampleHandler()
```

This is a generic metadata example, not a script-specific override. Initially support the token `double-click` for a single LButton/RButton/MButton/XButton1/XButton2 declaration, optionally with ordinary modifiers/prefixes and release. The backend validates eligibility; custom combinations, wheel and keyboard gestures are unsupported.

Grammar: one standalone line comment, optional leading spaces/tabs, exact marker/token, optional trailing whitespace, immediately followed by its static hotkey declaration. No intervening blank/comment/directive, duplicate marker, free-form label, expression or chord lookup. Bind within the same file and current declaration/context. Retain at most one pending annotation, capped at 256 characters; invalidate it on the next physical line. Ignore lookalikes inside strings, block comments and continuation text. Report malformed, unsupported, orphaned or ineligible annotations through existing partial-analysis issues and fall back to ordinary readable syntax. Bound annotation diagnostics across the complete script analysis, including traversed includes, to 32 individual issues plus one localized `PartialAnalysis` summary of the suppressed count. After reaching the budget, retain only that count, not per-line diagnostic records; consume malformed annotation lines without cascading duplicate chord diagnostics. This keeps hostile annotation density from exceeding the active snapshot validator's 20,000-issue array limit; preserve existing analysis/read bounds and unrelated issue behavior.

Append nullable `DeclaredGesture` to C# HotkeySnapshot (default null) and optional/null equivalent in TypeScript. Use a strict enum/token mapping, explicit JSON enum registration and existing source generation; no reflection or arbitrary localized wire strings. Update the active `Presentation.Electron/src/shared/validation.ts` hotkey shape with optional `declaredGesture: nullable(oneOf('DoubleClick'))`, matching the wire enum exactly. Keep strict rejection of unknown fields, gesture tokens and types. Older snapshots without this field behave normally. Presence means “gesto informado pelo script”, not an inferred/runtime-confirmed fact. Preserve Chord, NormalizedChord, source/line, context, remap and conflict flags exactly. No catalog schema migration.

The analyzer must preserve declaration-key bytes before stripping body strings/comments so supported punctuation survives extraction. Limit that correction to declaration recognition; keep include/depth/byte limits, cancellation and coverage behavior. Do not broaden execution or normalization.

## 3. Workstreams and permitted writes

| ID / owner | Exact scope | Deliverable and dependency |
| --- | --- | --- |
| H1 / backend implementer | `Presentation.WinUI/Services/HotkeyAnalyzer.cs`, `Presentation.WinUI/Models/ManagerContracts.cs`, `Backend/Protocol.cs`, `Tests.WinUI/Core/DomainChecks.cs`, `Tests.Backend/BackendChecks.cs` | Strict adjacent metadata, punctuation-safe extraction, nullable contract, synthetic analyzer/serialization regressions. No helper-body inference. |
| H2 / renderer implementer | New `Presentation.Electron/src/renderer/hotkeys.ts`; `format.ts` only if needed; `resources/pt-BR.ts`, `styles.css`; `components/ScriptList.tsx`, `Inspector.tsx`, `Hotkeys.tsx`; new `tests/renderer/hotkeys.test.ts`, existing `model.test.ts` only if integration requires it | Shared formatting and all three presentations/search. Depends on agreed H1 wire contract; mocks may use that contract before integration. |
| H3 / coordinator-owned external executor | Only the captured desktop script, staged first as `hotkeys-desktop-candidate-v1.ahk` in external evidence storage; exact live target comes from root's private source metadata | Add adjacent generic gesture metadata to the existing declaration. Replace the fixed timing default with the current Windows double-click interval through the existing configuration. Preserve empty-desktop filtering, click propagation, reset behavior, fade/manual controls, unrelated bytes and encoding/newline style. No spatial algorithm rewrite. |
| H4 / root integration owner | `Presentation.Electron/src/shared/contracts.ts`, `Presentation.Electron/src/shared/validation.ts`; existing backend/renderer test harness and fixture scenario files only as needed for focused integration; release metadata/package/version files under the combined 1.0.4 plan | Integrate the optional nullable allowed-token gesture contract and its strict transport validation together. Serialize shared files with the separate missing-script feature; bind final source/build/test/review/package hashes. No independent branch/worktree. |

H3 timing uses [GetDoubleClickTime](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getdoubleclicktime), which reports the configured mouse interval. Keep a bounded positive fallback if the query fails; do not change system settings. The root assigns any small isolated timing-fixture artifact outside Git.

Shared ownership collision points with the other feature: `ManagerContracts.cs`, `Backend/Protocol.cs`, Electron `contracts.ts` and `validation.ts`, `Inspector.tsx`, `ScriptList.tsx`, `resources/pt-BR.ts`, `styles.css`, and shared backend/renderer test scaffolding. One integration owner sequences edits to these files. Both `Presentation.Electron/src/shared/contracts.ts` and `Presentation.Electron/src/shared/validation.ts` belong to that integration owner; H1/H2 must not race those files. Other application files and operational/profile data remain outside scope.

## 4. Interface behavior

Show the same readable result in the list, Inspector and hotkeys page. Include formatted labels in hotkey-page search while retaining raw search. Keep raw syntax and prefix explanations reachable through a Fluent tooltip and keyboard-accessible details; do not rely on HTML title alone. Screen readers receive the complete readable instruction. Show gesture provenance in details, alongside source/context and existing conflict/partial badges.

Permit at most two readable lines in the fixed-height list row; full wording remains available on focus and in Inspector. Preserve the 68-pixel virtualization/scrolling contract. Inspector and hotkey-page labels wrap completely. Check long chords and double-click labels in narrow layouts and zoom before choosing modest column/CSS adjustments.

## 5. Acceptance and checks

1. Before production edits, add safe failing display regressions for the reported raw-string behavior using synthetic `!^#+u`, mouse-release and F13 inputs. No operational script execution is needed. Then implement and run the same checks plus affected suites.
2. Formatter table tests cover modifier order, side restrictions/AltGr without discarding physical distinctions, all mouse buttons/wheel directions, release vs arrow Up, ordered custom combinations, wildcard/pass-through/hook detail, literal terminal symbols and punctuation, F1/F13/F24, manual text chords, VK/SC, malformed/unknown input and safe escaping. Assert raw identity and conflict flags do not change.
3. Backend tests use harmless temporary source strings: valid adjacent metadata on left/right/middle buttons; generic renamed files/handlers; modifiers/release; invalid token/oversize/duplicates/orphan/context/include boundaries; comments/strings/continuations; UTF-8/BOM/UTF-16; cancellation and existing limits. Unknown shapes remain readable partial analysis. Test JSON round-trip and absent-field compatibility. In `tests/renderer/hotkeys.test.ts`, exercise the active hotkey/snapshot validators with the property absent, null and valid `DoubleClick`; reject unknown tokens and wrong scalar/object/array types. Verify the valid backend snapshot passes the active transport validation rather than only TypeScript compile-time types. Add a harmless within-1-MiB synthetic source with more than 32 malformed/orphan annotations: assert at most 32 individual annotation issues and one accurate overflow summary, retained ordinary hotkeys/partial coverage, and acceptance of the resulting real serialized snapshot by the active snapshot validator without a transport disconnect.
4. Renderer integration checks prove consistent labels, readable and raw search, accessible full text/details, retained provenance/context/conflict badges, raw manual editing, and row scrolling/selection. Exercise existing isolated Electron presentation fixtures with long labels at representative 1240/1000/740-pixel widths and 200% zoom. No private body/path enters fixtures or screenshots.
5. External candidate acceptance is separate: diff against preserved original; only metadata/timing changes; encoding/LF preserved; analyzer reads its generic gesture declaration. An inert AHK timing fixture verifies boundary/reset decisions with injected time and a stub action, never desktop effects. Actual desktop behavior is explicitly pending until an authorized later live exercise; do not run/reload the installed script.
6. The coordinator runs Electron typecheck, focused Node tests via the existing runner with a fresh absolute `--out`, backend build plus `--core`/affected `--backend` checks under the existing isolated-root harness, then the combined required checks. Retain complete logs/case outcomes and distinguish checks not run from passed checks. Reuse results only for identical relevant inputs.

## 6. Integration, recovery and completion

Order: fresh combined plan approval → contract/H1/H2 integration → focused checks → distinct fresh implementation review → approved 1.0.4 package and rollback preparation → root-controlled application and bounded validation → authorized Git/publication steps. Root owns external application, Git, installs, runtime effects and user questions.

External source replacement is guarded by the captured original hash and preserves a rollback copy outside Git. A changed live source is a conflict requiring a fresh candidate/review; never overwrite it. A successful atomic replacement confirms only file application. The already-running AHK process still uses its prior loaded script; no reload is authorized.

Feature acceptance means automatic notation translation plus explicit declared-gesture reading is demonstrated by isolated tests and active Electron views. It does not mean arbitrary gesture logic, all dynamic/includes or global shortcuts were resolved. No body recognizer, required hand-authored label for ordinary shortcuts, private fixture publication or operational process control is part of completion.

Material risks: punctuation ambiguity must degrade safely rather than misparse executable text; annotation drift is bounded by strict adjacency; long labels must not break virtualization; new nullable fields must remain compatible with existing fixtures/serialization. Original private logic was inspected only through the authorized snapshot; its runtime behavior is source-supported, not reproduced. Planning performed no build, test, source application or process action.
