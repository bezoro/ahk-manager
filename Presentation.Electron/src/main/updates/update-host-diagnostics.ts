import type { ChildProcess } from 'node:child_process';

const phases = new Set([
  'arguments',
  'host-location',
  'cache-ownership',
  'host-ownership',
  'host-lease',
  'host-integrity',
  'plan-lease',
  'plan-integrity',
  'plan-read',
  'plan-validation',
  'plan-location',
  'operation-ownership',
  'package-ownership',
  'installation-paths',
  'installation-ownership',
  'portable-initialization',
  'portable-data-initialization',
  'installation-parent-ownership',
  'operation-lock',
  'operation-log',
  'package',
  'fallback-package',
  'registration',
  'parent-identity',
  'installed-image',
  'cancellation',
  'readiness',
  'apply',
  'outcome',
]);
const codes = new Set([
  'cancelled',
  'invalid-data',
  'access-denied',
  'file-not-found',
  'directory-not-found',
  'sharing-violation',
  'lock-violation',
  'invalid-argument',
  'invalid-state',
  'native-error',
  'invalid-json',
  'security-error',
  'timeout',
  'io-error',
  'unsupported-filesystem',
  'cloud-file-unavailable',
  'redirected-path',
  'incomplete-package',
  'invalid-package',
]);
const maximumBytes = 4096;

export interface UpdateHostRefusal {
  schemaVersion: 1;
  phase: string;
  code: string;
}

/** Accept only the native host's finite diagnostic vocabulary, never arbitrary stderr. */
export function readUpdateHostRefusal(bytes: Buffer): UpdateHostRefusal | null {
  if (bytes.length > maximumBytes) return null;
  let result: UpdateHostRefusal | null = null;
  const lines = bytes.toString('utf8').split('\n');
  if (lines.pop()!.startsWith('AHK_UPDATE_REFUSAL:')) return null;
  for (const line of lines) {
    if (!line.startsWith('AHK_UPDATE_REFUSAL:')) continue;
    // The strict wire shape also rejects duplicate keys, control characters and extra fields.
    const match =
      /^AHK_UPDATE_REFUSAL:\{"schemaVersion":1,"phase":"([a-z-]{1,40})","code":"([a-z-]{1,32})"\}\r?$/.exec(
        line,
      );
    if (!match || !phases.has(match[1]!) || !codes.has(match[2]!) || result) return null;
    result = { schemaVersion: 1, phase: match[1]!, code: match[2]! };
  }
  return result;
}

/** Drain from process creation; retain at most 4 KiB and allow exit-time pipe data to arrive. */
export function observeUpdateHost(child: Pick<ChildProcess, 'stderr' | 'once'>): {
  refusal(): Promise<UpdateHostRefusal | null>;
} {
  let bytes = Buffer.alloc(0);
  let overflow = false;
  child.stderr?.on('data', (chunk: Buffer) => {
    if (overflow) return;
    if (!Buffer.isBuffer(chunk) || bytes.length + chunk.length > maximumBytes) {
      overflow = true;
      bytes = Buffer.alloc(0);
      return;
    }
    bytes = Buffer.concat([bytes, chunk]);
  });
  child.stderr?.on('error', () => {
    overflow = true;
    bytes = Buffer.alloc(0);
  });
  // A detached helper must not keep the Manager alive while it waits for normal exit.
  (
    child.stderr as (NonNullable<ChildProcess['stderr']> & { unref?: () => void }) | null
  )?.unref?.();
  const closed = new Promise<void>((resolve) => child.once('close', () => resolve()));
  return {
    async refusal(): Promise<UpdateHostRefusal | null> {
      let timer: ReturnType<typeof setTimeout> | undefined;
      try {
        await Promise.race([
          closed,
          new Promise<void>((resolve) => {
            timer = setTimeout(resolve, 250);
          }),
        ]);
      } finally {
        clearTimeout(timer);
      }
      return overflow ? null : readUpdateHostRefusal(bytes);
    },
  };
}

export function updateHostRefusalMessage(refusal: UpdateHostRefusal | null): string {
  const fallback = 'The update helper rejected the update. The installation has been preserved.';
  if (refusal?.schemaVersion !== 1 || !phases.has(refusal.phase) || !codes.has(refusal.code))
    return fallback;
  if (refusal.code === 'unsupported-filesystem')
    return 'Updates require NTFS storage for the application and update cache. ReFS, FAT32 and exFAT are not supported. Nothing was changed.';
  if (refusal.code === 'cloud-file-unavailable')
    return 'Some application or update files are not available locally. In OneDrive, choose "Always keep on this device", wait for syncing to finish, then try the update again. Nothing was changed.';
  if (refusal.code === 'redirected-path')
    return 'The application or update path contains a symbolic link, junction or unsupported redirect. Use a local folder without redirected path components. Nothing was changed.';
  if (refusal.phase === 'registration' && refusal.code === 'invalid-data')
    return 'This copy of Manager is not registered for in-app updates. Run Manager Setup once, then open Manager from its installed shortcut. Your profile and scripts will be preserved.';
  if (['installation-ownership', 'installation-parent-ownership'].includes(refusal.phase))
    return 'The installation folder or its parent could not be verified as a private, trusted location. Check its permissions or install Manager in the default per-user folder. Nothing was changed.';
  if (
    ['cache-ownership', 'host-ownership', 'operation-ownership', 'package-ownership'].includes(
      refusal.phase,
    )
  )
    return 'Windows could not verify access to the update cache. Check its permissions and try again. Nothing was changed.';
  return fallback;
}
