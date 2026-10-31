import fs from 'node:fs';
import path from 'node:path';
import { randomUUID } from 'node:crypto';
import type { DiagnosticExportResult } from '../../shared/contracts.js';
import { assertStoragePath } from '../storage/deployment.js';

interface DiagnosticExportPorts {
  chooseDestination(): Promise<string | null>;
  alive(): boolean;
  build(): { bytes: Buffer; complete: boolean; manifest: { issues: readonly unknown[] } };
  write(destination: string, bytes: Buffer): void;
}

/** Collect after the user's save choice; cancelled or stale dialogs never write an archive. */
export async function exportDiagnosticArchive(
  ports: DiagnosticExportPorts,
): Promise<DiagnosticExportResult | null> {
  const destination = await ports.chooseDestination();
  if (destination === null) return null;
  if (!ports.alive()) throw new Error('The document changed. Open Help and try again.');
  const archive = ports.build();
  if (!ports.alive()) throw new Error('The document changed. Open Help and try again.');
  ports.write(destination, archive.bytes);
  return { complete: archive.complete, issueCount: archive.manifest.issues.length };
}

type Stamp = fs.BigIntStats;
const identity = (stamp: Stamp): string => [stamp.dev, stamp.ino, stamp.birthtimeNs].join(':');
const version = (stamp: Stamp): string =>
  [identity(stamp), stamp.size, stamp.mtimeNs, stamp.ctimeNs, stamp.nlink, stamp.mode].join(':');
const regular = (stamp: Stamp): boolean =>
  stamp.isFile() && !stamp.isSymbolicLink() && stamp.nlink === 1n;
function inspect(destination: string): Stamp | null {
  try {
    const stamp = fs.lstatSync(destination, { bigint: true });
    if (!regular(stamp)) throw new Error('Unsafe archive destination');
    return stamp;
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === 'ENOENT') return null;
    throw error;
  }
}

/** Stage and flush before replacing the exact regular file chosen in the native save dialog. */
export function writeDiagnosticArchive(destination: string, bytes: Buffer): void {
  let temporary: string | null = null;
  let temporaryIdentity: string | null = null;
  let descriptor: number | null = null;
  try {
    if (
      !path.isAbsolute(destination) ||
      destination.startsWith('\\\\') ||
      !Buffer.isBuffer(bytes) ||
      bytes.length > 40 * 1024 * 1024
    )
      throw new Error('Unsafe archive destination');
    const parent = path.dirname(destination);
    assertStoragePath(parent);
    const directory = fs.lstatSync(parent, { bigint: true });
    if (!directory.isDirectory() || directory.isSymbolicLink())
      throw new Error('Unsafe archive destination');
    assertStoragePath(destination);
    const before = inspect(destination);
    temporary = path.join(parent, `.${path.basename(destination)}.${randomUUID()}.tmp`);
    descriptor = fs.openSync(
      temporary,
      fs.constants.O_WRONLY |
        fs.constants.O_CREAT |
        fs.constants.O_EXCL |
        (fs.constants.O_NOFOLLOW ?? 0),
      0o600,
    );
    const opened = fs.fstatSync(descriptor, { bigint: true });
    if (!regular(opened)) throw new Error('Unsafe archive destination');
    temporaryIdentity = identity(opened);
    fs.writeFileSync(descriptor, bytes);
    fs.fsyncSync(descriptor);
    const written = fs.fstatSync(descriptor, { bigint: true });
    fs.closeSync(descriptor);
    descriptor = null;
    assertStoragePath(parent);
    assertStoragePath(destination);
    const currentDirectory = fs.lstatSync(parent, { bigint: true });
    const current = inspect(destination);
    const staged = inspect(temporary);
    if (
      identity(directory) !== identity(currentDirectory) ||
      (before === null
        ? current !== null
        : current === null || version(before) !== version(current)) ||
      !staged ||
      identity(staged) !== temporaryIdentity ||
      version(staged) !== version(written) ||
      staged.size !== BigInt(bytes.length)
    )
      throw new Error('The archive destination changed');
    fs.renameSync(temporary, destination);
    temporary = null;
  } catch (error) {
    const failure = new Error(
      'The diagnostic archive could not be saved. Choose a writable local folder and try again.',
    );
    const code = (error as NodeJS.ErrnoException)?.code;
    if (code) Object.assign(failure, { code });
    throw failure;
  } finally {
    if (descriptor !== null)
      try {
        fs.closeSync(descriptor);
      } catch {
        /* Preserve the original failure. */
      }
    if (temporary && temporaryIdentity)
      try {
        const current = fs.lstatSync(temporary, { bigint: true });
        if (regular(current) && identity(current) === temporaryIdentity) fs.unlinkSync(temporary);
      } catch {
        /* A changed or inaccessible file remains untouched. */
      }
  }
}
