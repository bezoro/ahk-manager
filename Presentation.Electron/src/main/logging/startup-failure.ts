import fs from 'node:fs';
import path from 'node:path';
import { randomUUID } from 'node:crypto';
import { assertStoragePath, type DeploymentLayout } from '../storage/deployment.js';
import { PortableInitializationError } from '../desktop/storage-bootstrap.js';
import { parseStorageJson } from '../storage/json.js';

const applicationId = 'Bezoro.AhkManager.Electron';
const namePattern = /^startup-(\d{8}T\d{9}Z)-([0-9a-f-]{36})\.json$/;
const maximumBytes = 65536;
const maximumReports = 10;
const identity = (stamp: fs.BigIntStats): string =>
  [stamp.dev, stamp.ino, stamp.birthtimeNs].join(':');
const version = (stamp: fs.BigIntStats): string =>
  [identity(stamp), stamp.size, stamp.mtimeNs, stamp.ctimeNs, stamp.nlink].join(':');
const regular = (stamp: fs.BigIntStats): boolean =>
  stamp.isFile() && !stamp.isSymbolicLink() && stamp.nlink === 1n;
const exact = (value: unknown, keys: readonly string[]): value is Record<string, unknown> =>
  !!value &&
  typeof value === 'object' &&
  !Array.isArray(value) &&
  Object.keys(value).length === keys.length &&
  keys.every((key) => Object.hasOwn(value, key));

export interface StartupFailureContext {
  localAppDataDirectory: string;
  version: string;
  executablePath: string;
  layout: DeploymentLayout | null;
  login: boolean;
  background: boolean;
  error: unknown;
}
export interface StartupFailureReport {
  text: string;
  summary: string;
  file: string | null;
  directory: string | null;
}

export function startupFailureDirectory(localAppDataDirectory: string): string {
  return path.join(localAppDataDirectory, 'Bezoro', 'AhkManagerElectronDiagnostics', 'Startup');
}

function inspect(file: string): fs.BigIntStats | null {
  let descriptor: number | null = null;
  try {
    const match = namePattern.exec(path.basename(file));
    if (!match) return null;
    const before = fs.lstatSync(file, { bigint: true });
    if (!regular(before) || before.size < 1n || before.size > BigInt(maximumBytes)) return null;
    descriptor = fs.openSync(file, fs.constants.O_RDONLY | (fs.constants.O_NOFOLLOW ?? 0));
    if (version(fs.fstatSync(descriptor, { bigint: true })) !== version(before)) return null;
    const contents = Buffer.alloc(Number(before.size));
    let offset = 0;
    while (offset < contents.length) {
      const read = fs.readSync(descriptor, contents, offset, contents.length - offset, offset);
      if (!read) return null;
      offset += read;
    }
    const report = parseStorageJson(new TextDecoder('utf-8', { fatal: true }).decode(contents));
    if (!report || typeof report !== 'object' || Array.isArray(report)) return null;
    const value = report as Record<string, unknown>;
    if (
      !exact(value, [
        'schemaVersion',
        'applicationId',
        'kind',
        'id',
        'timestamp',
        'version',
        'executablePath',
        'deployment',
        'startup',
        'error',
      ]) ||
      value.schemaVersion !== 1 ||
      value.applicationId !== applicationId ||
      value.kind !== 'startup-failure' ||
      value.id !== match[2] ||
      typeof value.timestamp !== 'string' ||
      value.timestamp.replace(/[-:.]/g, '') !== match[1]
    )
      return null;
    if (
      !exact(value.startup, ['login', 'background']) ||
      typeof value.startup.login !== 'boolean' ||
      typeof value.startup.background !== 'boolean' ||
      typeof value.version !== 'string' ||
      typeof value.executablePath !== 'string' ||
      (value.deployment !== null &&
        !exact(value.deployment, ['mode', 'portableRoot', 'applicationDirectory', 'dataDirectory']))
    )
      return null;
    const problem = value.error;
    if (exact(problem, ['name', 'message', 'code', 'diagnostic'])) {
      if (
        problem.name !== 'PortableInitializationError' ||
        typeof problem.message !== 'string' ||
        !exact(problem.diagnostic, [
          'origin',
          'phase',
          'stage',
          'resource',
          'code',
          'exitCode',
          'errorCode',
          'hresult',
          'nativeError',
          'userSid',
          'administrator',
        ])
      )
        return null;
    } else if (
      !exact(problem, ['name', 'message', 'code']) ||
      problem.name !== 'StartupFailure' ||
      typeof problem.message !== 'string'
    )
      return null;
    const after = fs.fstatSync(descriptor, { bigint: true });
    const current = fs.lstatSync(file, { bigint: true });
    return regular(current) &&
      version(before) === version(after) &&
      version(before) === version(current)
      ? current
      : null;
  } catch {
    return null;
  } finally {
    if (descriptor !== null)
      try {
        fs.closeSync(descriptor);
      } catch {
        /* Read-only inspection. */
      }
  }
}

/** Independent of the portable data folder, so initialization refusals remain diagnosable. */
export function recordStartupFailure(context: StartupFailureContext): StartupFailureReport {
  const timestamp = new Date().toISOString();
  const id = randomUUID();
  const initialization =
    context.error instanceof PortableInitializationError ? context.error : null;
  const report = {
    schemaVersion: 1,
    applicationId,
    kind: 'startup-failure',
    id,
    timestamp,
    version: context.version,
    executablePath: context.executablePath,
    deployment: context.layout
      ? {
          mode: context.layout.mode,
          portableRoot: context.layout.portableRoot,
          applicationDirectory: context.layout.applicationDirectory,
          dataDirectory: context.layout.dataDirectory,
        }
      : null,
    startup: { login: context.login, background: context.background },
    error: initialization
      ? {
          name: initialization.name,
          message: initialization.message,
          code: initialization.code,
          diagnostic: initialization.diagnostic,
        }
      : {
          name: 'StartupFailure',
          message: 'Manager could not complete startup.',
          code:
            context.error &&
            typeof context.error === 'object' &&
            'code' in context.error &&
            ['ENOENT', 'EACCES', 'EPERM', 'EIO', 'ENOSPC', 'EROFS'].includes(
              String(context.error.code),
            )
              ? String(context.error.code)
              : null,
        },
  };
  const text = JSON.stringify(report, null, 2) + '\n';
  const diagnostic = initialization?.diagnostic;
  const summary = diagnostic
    ? [
        `Failure code: ${diagnostic.code}`,
        `Origin: ${diagnostic.origin}`,
        diagnostic.phase ? `Phase: ${diagnostic.phase}` : '',
        diagnostic.stage ? `Stage: ${diagnostic.stage}` : '',
        diagnostic.resource ? `Resource: ${diagnostic.resource}` : '',
        diagnostic.errorCode ? `Launch error: ${diagnostic.errorCode}` : '',
        diagnostic.nativeError !== null ? `Windows error: ${diagnostic.nativeError}` : '',
        diagnostic.hresult ? `HRESULT: ${diagnostic.hresult}` : '',
        diagnostic.userSid ? `Windows account SID: ${diagnostic.userSid}` : '',
        diagnostic.administrator !== null
          ? `Administrator token: ${diagnostic.administrator ? 'yes' : 'no'}`
          : '',
      ]
        .filter(Boolean)
        .join('\n')
    : 'Manager startup did not complete.';
  let descriptor: number | null = null;
  try {
    if (
      !path.isAbsolute(context.localAppDataDirectory) ||
      context.localAppDataDirectory.startsWith('\\\\') ||
      Buffer.byteLength(text) > maximumBytes
    )
      return { text, summary, file: null, directory: null };
    const directory = startupFailureDirectory(context.localAppDataDirectory);
    assertStoragePath(directory);
    fs.mkdirSync(directory, { recursive: true, mode: 0o700 });
    const original = fs.lstatSync(directory, { bigint: true });
    if (!original.isDirectory() || original.isSymbolicLink())
      throw new Error('Unsafe diagnostics directory');
    const check = () => {
      assertStoragePath(directory);
      const current = fs.lstatSync(directory, { bigint: true });
      if (
        !current.isDirectory() ||
        current.isSymbolicLink() ||
        identity(current) !== identity(original)
      )
        throw new Error('Diagnostics directory changed');
    };
    const names = fs.readdirSync(directory);
    if (names.length > 256) return { text, summary, file: null, directory: null };
    const retained = names
      .filter((name) => namePattern.test(name))
      .sort()
      .map((name) => ({
        file: path.join(directory, name),
        stamp: inspect(path.join(directory, name)),
      }))
      .filter((item) => item.stamp !== null);
    while (retained.length >= maximumReports) {
      const oldest = retained.shift()!;
      check();
      const current = inspect(oldest.file);
      if (!current || version(current) !== version(oldest.stamp!))
        throw new Error('Retained report changed');
      fs.unlinkSync(oldest.file);
    }
    check();
    const file = path.join(directory, `startup-${timestamp.replace(/[-:.]/g, '')}-${id}.json`);
    descriptor = fs.openSync(
      file,
      fs.constants.O_WRONLY |
        fs.constants.O_CREAT |
        fs.constants.O_EXCL |
        (fs.constants.O_NOFOLLOW ?? 0),
      0o600,
    );
    const opened = fs.fstatSync(descriptor, { bigint: true });
    if (!regular(opened)) throw new Error('Unsafe diagnostics file');
    fs.writeFileSync(descriptor, text);
    fs.fsyncSync(descriptor);
    check();
    const current = fs.lstatSync(file, { bigint: true });
    if (
      !regular(current) ||
      identity(current) !== identity(opened) ||
      current.size !== BigInt(Buffer.byteLength(text))
    )
      throw new Error('Diagnostics file changed');
    return { text, summary, file, directory };
  } catch {
    return { text, summary, file: null, directory: null };
  } finally {
    if (descriptor !== null)
      try {
        fs.closeSync(descriptor);
      } catch {
        /* Never replace the original startup error. */
      }
  }
}
