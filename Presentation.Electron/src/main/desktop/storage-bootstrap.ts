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

const stages = [
  'resolve-layout',
  'retain-root',
  'retain-data',
  'open-marker',
  'verify-marker',
  'verify-helper',
  'prepare-app',
  'verify-root-members',
  'open-inventory',
  'verify-version',
  'protect-root',
  'protect-app',
  'protect-directory',
  'protect-file',
  'protect-descriptor',
  'protect-marker',
  'verify-root-access',
  'verify-app-access',
  'prepare-data',
  'create-data',
  'verify-data-access',
] as const;
const resources = [
  'root',
  'application',
  'data',
  'marker',
  'descriptor',
  'helper',
  'payload-directory',
  'payload-file',
] as const;
const systemCodes = new Set([
  'ENOENT',
  'EACCES',
  'EPERM',
  'ENOBUFS',
  'EIO',
  'EAGAIN',
  'EMFILE',
  'ENFILE',
  'EINVAL',
  'ETIMEDOUT',
  'ENOMEM',
]);
type InitializationPhase = 'portable-initialization' | 'portable-data-initialization';
export interface PortableInitializationDiagnostic {
  origin: 'spawn' | 'native' | 'unknown';
  phase: InitializationPhase | null;
  stage: (typeof stages)[number] | null;
  resource: (typeof resources)[number] | null;
  code: InitializationCode;
  exitCode: number | null;
  errorCode: string | null;
  hresult: string | null;
  nativeError: number | null;
  userSid: string | null;
  administrator: boolean | null;
}

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
      return 'Some Manager or data files are not available locally. Make the complete portable folder available offline in your sync app, wait for syncing to finish, then open Manager again.';
    case 'redirected-path':
      return 'The portable app or data path contains a symbolic link, junction or unsupported redirect. Use a local folder without redirected path components.';
    case 'access-denied':
      return 'Windows denied access during portable startup. Copy diagnostic details to identify the affected folder or helper.';
    case 'incomplete-package':
      return 'The portable package is incomplete. Extract the entire portable ZIP into its own folder, then open Bezoro.AhkManager.exe from that folder.';
    case 'invalid-package':
      return 'The portable package or its data settings are invalid. Extract a fresh portable ZIP into a separate folder. Keep your existing Data folder and portable.json for recovery.';
    case 'data-unavailable':
      return 'The configured data folder could not be opened or verified. Check that its drive is available, its parent folder exists, and your Windows account can access it. Your existing data and recovery files were preserved.';
    case 'files-in-use':
      return 'Another application is using the portable app or data files. Close other Manager windows and let your sync app finish syncing, then try again.';
    default:
      return 'The portable folder could not be prepared. Your existing data was preserved. Check folder access and try again.';
  }
}

export class PortableInitializationError extends Error {
  readonly diagnostic: PortableInitializationDiagnostic;
  constructor(
    readonly code: InitializationCode,
    diagnostic?: PortableInitializationDiagnostic,
  ) {
    super(initializationMessage(code));
    this.name = 'PortableInitializationError';
    this.diagnostic = diagnostic ?? {
      origin: 'unknown',
      phase: null,
      stage: null,
      resource: null,
      code,
      exitCode: null,
      errorCode: null,
      hresult: null,
      nativeError: null,
      userSid: null,
      administrator: null,
    };
  }
}

function diagnosticContext(
  stderr: string,
  phase: InitializationPhase,
): Pick<
  PortableInitializationDiagnostic,
  'stage' | 'resource' | 'hresult' | 'nativeError' | 'userSid' | 'administrator'
> {
  const empty = {
    stage: null,
    resource: null,
    hresult: null,
    nativeError: null,
    userSid: null,
    administrator: null,
  };
  if (Buffer.byteLength(stderr, 'utf8') > 4096) return empty;
  const lines = stderr.split('\n');
  if (lines.pop()!.startsWith('AHK_PORTABLE_INITIALIZATION:')) return empty;
  let context: ReturnType<typeof diagnosticContext> | null = null;
  for (const line of lines) {
    if (!line.startsWith('AHK_PORTABLE_INITIALIZATION:')) continue;
    const match =
      /^AHK_PORTABLE_INITIALIZATION:\{"schemaVersion":1,"phase":"(portable-initialization|portable-data-initialization)","stage":"([a-z-]{1,32})","resource":"([a-z-]{1,24})","hresult":"(0x[0-9A-F]{8})","nativeError":(null|0|[1-9]\d{0,4}),"userSid":(null|"S-\d+(?:-\d+){1,15}"),"administrator":(null|true|false)\}\r?$/.exec(
        line,
      );
    if (
      !match ||
      match[1] !== phase ||
      !stages.includes(match[2] as (typeof stages)[number]) ||
      !resources.includes(match[3] as (typeof resources)[number]) ||
      context ||
      (match[5] !== 'null' && Number(match[5]) > 65535) ||
      (match[6] !== 'null' && match[6]!.length - 2 > 184)
    )
      return empty;
    context = {
      stage: match[2] as (typeof stages)[number],
      resource: match[3] as (typeof resources)[number],
      hresult: match[4]!,
      nativeError: match[5] === 'null' ? null : Number(match[5]),
      userSid: match[6] === 'null' ? null : match[6]!.slice(1, -1),
      administrator: match[7] === 'null' ? null : match[7] === 'true',
    };
  }
  return context ?? empty;
}

function failure(result: ReturnType<StartInitializer>): PortableInitializationError {
  const code = initializationCode(result);
  const status = result.status;
  const exitCode =
    typeof status === 'number' &&
    Number.isInteger(status) &&
    status >= -2147483648 &&
    status <= 2147483647
      ? status
      : null;
  const rawSystemCode = (result.error as NodeJS.ErrnoException | undefined)?.code;
  if (result.error)
    return new PortableInitializationError(code, {
      origin: 'spawn',
      phase: null,
      stage: null,
      resource: 'helper',
      code,
      exitCode,
      errorCode:
        typeof rawSystemCode === 'string' && systemCodes.has(rawSystemCode) ? rawSystemCode : null,
      hresult: null,
      nativeError: null,
      userSid: null,
      administrator: null,
    });
  const stderr = typeof result.stderr === 'string' ? result.stderr : '';
  const refusal = readUpdateHostRefusal(Buffer.from(stderr, 'utf8'));
  const phase =
    refusal?.phase === 'portable-initialization' ||
    refusal?.phase === 'portable-data-initialization'
      ? refusal.phase
      : null;
  return new PortableInitializationError(code, {
    origin: phase ? 'native' : 'unknown',
    phase,
    ...(phase
      ? diagnosticContext(stderr, phase)
      : {
          stage: null,
          resource: null,
          hresult: null,
          nativeError: null,
          userSid: null,
          administrator: null,
        }),
    code,
    exitCode,
    errorCode: null,
  });
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
    throw failure({
      status: null,
      stderr: '',
      error:
        error instanceof Error
          ? error
          : Object.assign(new Error('Initializer could not be started.'), {
              code: (error as NodeJS.ErrnoException | null)?.code,
            }),
    });
  }
  if (result.error || result.status !== 0) throw failure(result);
}
