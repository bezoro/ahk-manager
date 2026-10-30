import fs from 'node:fs/promises';
import path from 'node:path';
import { createHash, randomUUID } from 'node:crypto';
import type { ShortcutDetails } from 'electron';
import { assertStoragePath, samePath, type DeploymentLayout } from '../storage/deployment.js';

const applicationId = 'Bezoro.AhkManager.Electron';
const description = 'Manage AutoHotkey scripts with AHK Manager Portable.';
const executableName = 'Bezoro.AhkManager.exe';
const shortcutName = 'AHK Manager Portable.lnk';

export interface PortableShortcutBoundary {
  platform: NodeJS.Platform;
  appDataDirectory: string;
  permitted(): boolean;
  read(file: string): ShortcutDetails;
  write(file: string, operation: 'create', details: ShortcutDetails): boolean;
  diagnostic?(event: PortableShortcutDiagnostic): void;
}

export type PortableShortcutPhase =
  | 'validation'
  | 'launcher'
  | 'directory'
  | 'existing'
  | 'stage-create'
  | 'native-write'
  | 'stage-verify'
  | 'rename'
  | 'backup-verify'
  | 'publish'
  | 'cleanup'
  | 'restore';
export type PortableShortcutDiagnosticCode =
  | 'EACCES'
  | 'EPERM'
  | 'EBUSY'
  | 'ENOENT'
  | 'EEXIST'
  | 'EIO'
  | 'ENOSPC'
  | 'ENOTDIR'
  | 'EISDIR'
  | 'EINVAL'
  | 'StorageFailure'
  | 'CapabilityUnverified'
  | 'DispatchFailed'
  | 'EffectOwnerUnavailable'
  | 'IdentityChanged';
export interface PortableShortcutDiagnostic {
  phase: PortableShortcutPhase;
  code: PortableShortcutDiagnosticCode;
  attempt: number;
  retrying: boolean;
}

export type PortableShortcutResult =
  'not-applicable' | 'created' | 'updated' | 'unchanged' | 'preserved' | 'failed';

interface FileStamp {
  identity: string;
  contents: string;
  bytes: number;
  links: bigint;
  version: string;
}
type ShortcutEntry =
  { kind: 'missing' | 'foreign' } | { kind: 'owned'; stamp: FileStamp; details: ShortcutDetails };

const missing = (error: unknown): boolean => (error as NodeJS.ErrnoException)?.code === 'ENOENT';
const renameBackoff = [40, 80, 160, 320, 640] as const;
const retryableRename = (error: unknown): boolean =>
  ['EACCES', 'EPERM', 'EBUSY'].includes((error as NodeJS.ErrnoException)?.code ?? '');
function diagnosticCode(error: unknown): PortableShortcutDiagnosticCode {
  const code = (error as NodeJS.ErrnoException)?.code;
  return [
    'EACCES',
    'EPERM',
    'EBUSY',
    'ENOENT',
    'EEXIST',
    'EIO',
    'ENOSPC',
    'ENOTDIR',
    'EISDIR',
    'EINVAL',
  ].includes(code ?? '')
    ? (code as PortableShortcutDiagnosticCode)
    : 'StorageFailure';
}
function report(
  boundary: PortableShortcutBoundary,
  phase: PortableShortcutPhase,
  code: PortableShortcutDiagnosticCode,
  attempt = 0,
  retrying = false,
): void {
  try {
    boundary.diagnostic?.({ phase, code, attempt, retrying });
  } catch {
    /* Diagnostic callbacks never change shortcut publication or recovery. */
  }
}

async function fileStamp(file: string): Promise<FileStamp> {
  assertStoragePath(file);
  const before = await fs.lstat(file, { bigint: true });
  const state = (stat: typeof before): string =>
    [
      stat.dev,
      stat.ino,
      stat.birthtimeNs,
      stat.size,
      stat.mtimeNs,
      stat.ctimeNs,
      stat.nlink,
      stat.mode,
    ].join(':');
  if (
    !before.isFile() ||
    before.isSymbolicLink() ||
    before.nlink < 1n ||
    before.nlink > 2n ||
    before.size > 1024n * 1024n
  )
    throw new Error('The shortcut is not a regular owned file.');
  const handle = await fs.open(file, 'r');
  try {
    if (state(await handle.stat({ bigint: true })) !== state(before))
      throw new Error('The shortcut changed while it was opened.');
    // Read at most the declared size plus one byte, even if another writer
    // grows the file. Empty bytes are valid only for our pre-write stage.
    const buffer = Buffer.alloc(Number(before.size) + 1);
    let bytes = 0;
    while (bytes < buffer.length) {
      const chunk = await handle.read(buffer, bytes, buffer.length - bytes, null);
      if (!chunk.bytesRead) break;
      bytes += chunk.bytesRead;
    }
    const after = await handle.stat({ bigint: true });
    const current = await fs.lstat(file, { bigint: true });
    if (
      bytes !== Number(before.size) ||
      state(after) !== state(before) ||
      state(current) !== state(before) ||
      current.isSymbolicLink()
    )
      throw new Error('The shortcut changed while its bytes were verified.');
    return {
      identity: [before.dev, before.ino, before.birthtimeNs].join(':'),
      contents: createHash('sha256').update(buffer.subarray(0, bytes)).digest('hex'),
      bytes,
      links: before.nlink,
      version: state(before),
    };
  } finally {
    await handle.close();
  }
}

const sameStamp = (first: FileStamp, second: FileStamp): boolean =>
  first.identity === second.identity && first.contents === second.contents;

function owned(details: ShortcutDetails): boolean {
  return (
    details.appUserModelId === applicationId &&
    details.description === description &&
    details.args === '--foreground' &&
    details.iconIndex === 0 &&
    typeof details.target === 'string' &&
    path.isAbsolute(details.target) &&
    !details.target.startsWith('\\\\') &&
    path.basename(details.target).toLowerCase() === executableName.toLowerCase() &&
    typeof details.cwd === 'string' &&
    path.isAbsolute(details.cwd) &&
    samePath(details.cwd, path.dirname(details.target)) &&
    typeof details.icon === 'string' &&
    path.isAbsolute(details.icon) &&
    samePath(details.icon, details.target)
  );
}

async function inspect(file: string, boundary: PortableShortcutBoundary): Promise<ShortcutEntry> {
  try {
    const before = await fileStamp(file);
    if (before.links !== 1n || before.bytes < 1) return { kind: 'foreign' };
    const details = boundary.read(file);
    const after = await fileStamp(file);
    return owned(details) &&
      after.links === 1n &&
      sameStamp(before, after) &&
      before.version === after.version
      ? { kind: 'owned', stamp: after, details }
      : { kind: 'foreign' };
  } catch (error) {
    return { kind: missing(error) ? 'missing' : 'foreign' };
  }
}

async function removeExact(file: string, expected: FileStamp): Promise<void> {
  if (sameStamp(await fileStamp(file), expected)) await fs.unlink(file);
}

async function renameOwned(
  source: string,
  backup: string,
  expected: FileStamp,
  boundary: PortableShortcutBoundary,
): Promise<'renamed' | 'preserved' | 'not-applicable' | 'failed'> {
  for (let attempt = 1; attempt <= renameBackoff.length + 1; attempt++) {
    if (!boundary.permitted()) {
      report(boundary, 'rename', 'EffectOwnerUnavailable', attempt);
      return 'not-applicable';
    }
    const current = await inspect(source, boundary);
    if (current.kind !== 'owned' || !sameStamp(expected, current.stamp)) {
      report(boundary, 'rename', 'IdentityChanged', attempt);
      return 'preserved';
    }
    assertStoragePath(backup);
    try {
      await fs.lstat(backup);
      report(boundary, 'rename', 'EEXIST', attempt);
      return 'preserved';
    } catch (error) {
      if (!missing(error)) throw error;
    }
    if (!boundary.permitted()) {
      report(boundary, 'rename', 'EffectOwnerUnavailable', attempt);
      return 'not-applicable';
    }
    try {
      await fs.rename(source, backup);
      return 'renamed';
    } catch (error) {
      const retrying = retryableRename(error) && attempt <= renameBackoff.length;
      report(boundary, 'rename', diagnosticCode(error), attempt, retrying);
      if (!retrying) return 'failed';
      await new Promise((resolve) => setTimeout(resolve, renameBackoff[attempt - 1]));
    }
  }
  return 'preserved';
}

/** A current-user Start Menu link also makes portable Manager visible to launchers
 * that index Windows applications. Failures never block Manager startup. */
export async function ensurePortableSearchShortcut(
  layout: DeploymentLayout,
  boundary: PortableShortcutBoundary,
): Promise<PortableShortcutResult> {
  let temporary: { file: string; stamp: FileStamp } | null = null;
  let previous: { file: string; stamp: FileStamp } | null = null;
  let destination: string | null = null;
  let published = false;
  let phase: PortableShortcutPhase = 'validation';
  try {
    if (boundary.platform !== 'win32' || layout.mode !== 'portable' || !boundary.permitted())
      return 'not-applicable';
    if (
      !layout.portableRoot ||
      !samePath(layout.launchExecutable, path.join(layout.portableRoot, executableName)) ||
      !path.isAbsolute(boundary.appDataDirectory) ||
      boundary.appDataDirectory.startsWith('\\\\')
    ) {
      report(boundary, phase, 'CapabilityUnverified');
      return 'failed';
    }
    phase = 'launcher';
    assertStoragePath(layout.launchExecutable);
    const launcher = await fs.lstat(layout.launchExecutable, { bigint: true });
    if (
      !launcher.isFile() ||
      launcher.isSymbolicLink() ||
      launcher.nlink !== 1n ||
      launcher.size < 1n
    ) {
      report(boundary, phase, 'CapabilityUnverified');
      return 'failed';
    }
    phase = 'directory';
    const directory = path.join(
      boundary.appDataDirectory,
      'Microsoft',
      'Windows',
      'Start Menu',
      'Programs',
    );
    assertStoragePath(directory);
    if (!boundary.permitted()) return 'not-applicable';
    await fs.mkdir(directory, { recursive: true, mode: 0o700 });
    assertStoragePath(directory);
    destination = path.join(directory, shortcutName);
    phase = 'existing';
    const existing = await inspect(destination, boundary);
    if (existing.kind === 'foreign') return 'preserved';
    if (existing.kind === 'owned' && samePath(existing.details.target, layout.launchExecutable))
      return 'unchanged';
    const details: ShortcutDetails = {
      target: layout.launchExecutable,
      cwd: layout.portableRoot,
      args: '--foreground',
      description,
      icon: layout.launchExecutable,
      iconIndex: 0,
      appUserModelId: applicationId,
    };
    if (!boundary.permitted()) return 'not-applicable';
    phase = 'stage-create';
    const staging = path.join(directory, `.ahk-manager-${randomUUID()}.lnk`);
    await fs.writeFile(staging, '', { flag: 'wx', mode: 0o600 });
    temporary = { file: staging, stamp: await fileStamp(staging) };
    if (!boundary.permitted()) return 'not-applicable';
    let written = false;
    let writeFailed = false;
    phase = 'native-write';
    try {
      written = boundary.write(staging, 'create', details);
    } catch (error) {
      writeFailed = true;
      report(boundary, phase, diagnosticCode(error));
      /* A failed shell call can still leave complete shortcut bytes behind. */
    }
    if (!written && !writeFailed) report(boundary, phase, 'DispatchFailed');
    phase = 'stage-verify';
    const staged = await inspect(staging, boundary);
    if (staged.kind !== 'owned' || !samePath(staged.details.target, layout.launchExecutable)) {
      report(boundary, phase, 'CapabilityUnverified');
      return 'failed';
    }
    if (written || staged.stamp.identity === temporary.stamp.identity)
      temporary.stamp = staged.stamp;
    if (!written) return 'failed';
    if (!boundary.permitted()) return 'not-applicable';
    if (existing.kind === 'owned') {
      const backup = path.join(directory, `.ahk-manager-previous-${randomUUID()}.lnk`);
      phase = 'rename';
      const renamed = await renameOwned(destination, backup, existing.stamp, boundary);
      if (renamed !== 'renamed') return renamed;
      phase = 'backup-verify';
      previous = { file: backup, stamp: await fileStamp(backup) };
      const retained = await inspect(backup, boundary);
      if (retained.kind !== 'owned' || !sameStamp(existing.stamp, retained.stamp)) {
        report(boundary, phase, 'IdentityChanged');
        return 'preserved';
      }
    }
    if (!boundary.permitted()) return 'not-applicable';
    phase = 'publish';
    // link publishes without replacing a shortcut that appeared concurrently.
    await fs.link(staging, destination);
    published = true;
    if (previous) {
      phase = 'cleanup';
      await removeExact(previous.file, previous.stamp);
    }
    return existing.kind === 'missing' ? 'created' : 'updated';
  } catch (error) {
    report(boundary, phase, diagnosticCode(error));
    return 'failed';
  } finally {
    if (previous && !published && destination) {
      // Restore without replacing a concurrent foreign entry. If the path is
      // occupied, retain the previous bytes in the backup for recovery.
      try {
        if (sameStamp(await fileStamp(previous.file), previous.stamp)) {
          await fs.link(previous.file, destination);
          await removeExact(previous.file, previous.stamp);
        }
      } catch (error) {
        report(boundary, 'restore', diagnosticCode(error));
        /* Every conflicting shortcut and the previous bytes remain intact. */
      }
    }
    if (temporary)
      await removeExact(temporary.file, temporary.stamp).catch((error) => {
        report(boundary, 'cleanup', diagnosticCode(error));
      });
  }
}
