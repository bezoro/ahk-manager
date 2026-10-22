# Safe Brush checks

Run from the repository root as a normal interactive user:

```powershell
pwsh -NoProfile -File .\tools\Run-SafeTests.ps1
```

The command accepts no arguments and never builds. It requires this reviewed prepared package and refuses stale/missing source, binaries or fixture inputs. Source changes require explicit package preparation by its owner.

Fixed plan: full stopped-recovery152 assertions, confirmation-final-read8, registered-format settlement65, manual-UAC fake47, then adapted AHK routing16 plus classification6. Total294 assertions in five feature suites; AHK generation is a separate preparation record. Expected counts are checked against exact bounded PASS records and process exit codes. No default/native/task-probe selectors are forwarded.

A normal-token proof is required before tests/output creation. Inputs are pinned with portable relative paths, no reparse paths, opened read-only without write/delete sharing, and rehashed before each process and at completion. Manifest has a separate checksum companion; the external reviewed freeze pins both without a self-hash cycle. Prepared production DLLs are test dependencies only under tools, not app publication. Fixed AHK v2 runs a private generated script; it checks A_IsAdmin=false and never registers hotkeys, launches applications or sends keys.

A unique directory in the current temporary root retains individual stdout/stderr, suite JSON and atomic summary.json with timings, token proof, identities, exit/timeout, counts and input/output hashes. The command prints that summary and returns nonzero on any failure. Elevated/unknown-token rejection happens before a run directory; failure is reported on stdout. Nothing is recursively cleaned up.

Every child is started with structured ArgumentList and bounded waits. Timeout may terminate only the same still-held returned child after PID/native creation/image/liveness revalidation; never a process name, tree, existing Manager or AHK. Diagnostics remain. If identity cannot be proved, the runner refuses termination.

Owner-only deterministic failure verification:

```powershell
pwsh -NoProfile -File .\Tests.SafeRunner\Run-FailureChecks.ps1
```

This invokes only fixed benign workers and exercises unknown arguments, scripted elevated/unknown token preflight, path escape, modified private input, failing/missing PASS and held-child timeout while another owned worker stays alive. It does not provide a public executable/selector/root override. Scripted elevation rejection is not a real elevated/UAC test.

These tests prove fake/offscreen UI/domain/orchestration and adapted captured Brush classification/routing. They do not prove physical PowerToys/device mapping, actual foreground selection, delivery/UIPI, secure desktop consent, real task registration or elevated process effects. Captured IsPaintingApp observation failure currently falls through to volume routing; the fixture records that behavior, not a product fix. UAC settings, installed Brush, task/security, live profile and running applications are unchanged.
