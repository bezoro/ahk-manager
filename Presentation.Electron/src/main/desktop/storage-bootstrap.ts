import {
  spawnSync,
  type SpawnSyncOptionsWithStringEncoding,
  type SpawnSyncReturns,
} from 'node:child_process';
import path from 'node:path';
import type { DeploymentLayout } from '../storage/deployment.js';
import { readUpdateHostRefusal } from '../updates/update-host-diagnostics.js';

type InitializationCode =
  | 'unsupported-filesystem'
  | 'cloud-file-unavailable'
  | 'redirected-path'
  | 'access-denied'
  | 'incomplete-package'
  | 'invalid-package'
  | 'data-unavailable'
  | 'files-in-use'
  | 'unknown';

type StartInitializer = (
  executable: string,
  args: readonly string[],
  options: SpawnSyncOptionsWithStringEncoding,
) => Pick<SpawnSyncReturns<string>, 'status' | 'error' | 'stderr'>;

function initializationMessage(code: InitializationCode): string {
  switch (code) {
    case 'unsupported-filesystem':
      return 'Portable Manager requires NTFS storage for its app and data folders. ReFS, FAT32 and exFAT are not supported. Extract the portable ZIP into an NTFS folder and try again.';
    case 'cloud-file-unavailable':
      return 'Some Manager or data files are not available locally. In OneDrive, choose "Always keep on this device", wait for syncing to finish, then open Manager again.';
    case 'redirected-path':
      return 'The portable app or data path contains a symbolic link, junction or unsupported redirect. Use a local folder without redirected path components.';
    case 'access-denied':
      return 'Windows could not verify private access to the portable app or data folder. Choose a folder owned by your Windows account and check its permissions.';
    case 'incomplete-package':
      return 'The portable package is incomplete. Extract the entire portable ZIP into its own folder, then open Bezoro.AhkManager.exe from that folder.';
    case 'invalid-package':
      return 'The portable package or its data settings are invalid. Extract a fresh portable ZIP into a separate folder. Keep your existing Data folder and portable.json for recovery.';
    case 'data-unavailable':
      return 'The configured data folder could not be opened or verified. Check that its drive is available, its parent folder exists, and your Windows account can access it. Your existing data and recovery files were preserved.';
    case 'files-in-use':
      return 'Another application is using the portable app or data files. Close other Manager windows and let OneDrive finish syncing, then try again.';
    default:
      return 'The portable folder could not be prepared. Your existing data was preserved. Check folder access and try again.';
  }
}

export class PortableInitializationError extends Error {
  constructor(readonly code: InitializationCode) {
    super(initializationMessage(code));
    this.name = 'PortableInitializationError';
  }
}

function spawnFailureCode(error: unknown): InitializationCode {
  const code = (error as NodeJS.ErrnoException | null)?.code;
  if (code === 'ENOENT') return 'incomplete-package';
  if (code === 'EACCES' || code === 'EPERM') return 'access-denied';
  return 'unknown';
}

function initializationCode(
  result: Pick<SpawnSyncReturns<string>, 'status' | 'error' | 'stderr'>,
): InitializationCode {
  if (result.error) return spawnFailureCode(result.error);
  const refusal =
    typeof result.stderr === 'string'
      ? readUpdateHostRefusal(Buffer.from(result.stderr, 'utf8'))
      : null;
  if (
    refusal?.phase !== 'portable-initialization' &&
    refusal?.phase !== 'portable-data-initialization'
  )
    return 'unknown';
  if (
    refusal.phase === 'portable-data-initialization' &&
    ['file-not-found', 'directory-not-found', 'invalid-data', 'invalid-json'].includes(refusal.code)
  )
    return 'data-unavailable';
  switch (refusal.code) {
    case 'unsupported-filesystem':
    case 'cloud-file-unavailable':
    case 'redirected-path':
    case 'access-denied':
    case 'incomplete-package':
    case 'invalid-package':
      return refusal.code;
    case 'sharing-violation':
    case 'lock-violation':
      return 'files-in-use';
    default:
      return 'unknown';
  }
}

/** Runs before Chromium opens data files; initialization never targets an ancestor folder. */
export function initializePortableStorage(
  layout: DeploymentLayout,
  start: StartInitializer = spawnSync,
): void {
  if (layout.mode !== 'portable') return;
  const helper = path.join(
    layout.applicationDirectory,
    'backend',
    'update',
    'AhkManager.UpdateHost.exe',
  );
  let result: ReturnType<StartInitializer>;
  try {
    result = start(helper, ['--initialize-portable'], {
      shell: false,
      windowsHide: true,
      stdio: ['ignore', 'ignore', 'pipe'],
      encoding: 'utf8',
      maxBuffer: 4096,
    });
  } catch (error) {
    throw new PortableInitializationError(spawnFailureCode(error));
  }
  if (result.error || result.status !== 0)
    throw new PortableInitializationError(initializationCode(result));
}
