import fs from 'node:fs';
import path from 'node:path';
import { AsyncLocalStorage } from 'node:async_hooks';
import { randomUUID } from 'node:crypto';
import { performance } from 'node:perf_hooks';
import { guid, operation, outcome, reason } from '../../shared/validation.js';
import { assertStoragePath } from '../storage/deployment.js';

const applicationId = 'Bezoro.AhkManager.Electron';
const filePattern = /^commands-(\d{8}T\d{9}Z)-([0-9a-f-]{36})-(\d{6})\.jsonl$/;
const sources = new Set(['ui', 'backend', 'tray', 'lifecycle', 'updates', 'storage']);
const protocolCodes = new Set([
  'Ok',
  'InvalidFrame',
  'InvalidRequest',
  'NotAuthenticated',
  'ParentMismatch',
  'UnknownAction',
  'QueueFull',
  'IdConflict',
  'RequestExpired',
  'FrameTooLarge',
  'ShuttingDown',
  'InternalFailure',
]);
const states = new Set([
  'idle',
  'checking',
  'downloading',
  'installing',
  'error',
  'PendingApproval',
  'Ready',
  'CommitPending',
  'CommitAcked',
  'Cancelled',
  'Denied',
  'Failed',
  'Expired',
  'Completed',
  'Unknown',
]);
const errorNames = new Set([
  'Error',
  'TypeError',
  'RangeError',
  'SyntaxError',
  'ReferenceError',
  'URIError',
  'EvalError',
  'AggregateError',
  'AbortError',
  'UnknownAfterDispatchError',
  'BackendProtocolError',
]);
const systemCodes = new Set([
  'EACCES',
  'EPERM',
  'ENOENT',
  'EEXIST',
  'ENOSPC',
  'EDQUOT',
  'EIO',
  'EBUSY',
  'EMFILE',
  'ENFILE',
  'ENOTDIR',
  'EISDIR',
  'ELOOP',
  'EROFS',
  'EINVAL',
  'ETIMEDOUT',
  'ECONNRESET',
  'ECONNREFUSED',
  'ENOTFOUND',
  'EAI_AGAIN',
  'EPIPE',
  'ERR_ABORTED',
  'ERR_INVALID_ARG_TYPE',
  'ERR_INVALID_ARG_VALUE',
]);

export type CommandSource = 'ui' | 'backend' | 'tray' | 'lifecycle' | 'updates' | 'storage';
export interface CommandDetails {
  operation?: string;
  scriptId?: string;
  version?: string;
  channel?: 'stable' | 'test';
  runAtLogin?: boolean | null;
  runAsAdministrator?: boolean;
  launchManagerAtLogin?: boolean;
  launchManagerAsAdministrator?: boolean;
  startInTray?: boolean;
}
export interface CommandLogStatus {
  available: boolean;
  directory: string;
  failure: 'unavailable' | 'unsafe-path' | 'retention-limit' | 'closed' | null;
}
export interface CommandLogBoundary {
  readonly directory: string;
  run<T>(
    source: CommandSource,
    command: string,
    work: () => T | Promise<T>,
    details?: CommandDetails,
  ): Promise<T>;
  status(): CommandLogStatus;
  close(): void;
}
export interface CommandLogOptions {
  maximumFileBytes?: number;
  maximumFiles?: number;
}
type RecordValue = string | number | boolean | null;
type Fields = Record<string, RecordValue>;
type Stamp = fs.BigIntStats;
interface RetainedFile {
  file: string;
  stamp: Stamp;
}

const identity = (value: Stamp): string => [value.dev, value.ino, value.birthtimeNs].join(':');
const version = (value: Stamp): string =>
  [identity(value), value.size, value.mtimeNs, value.ctimeNs, value.nlink, value.mode].join(':');
// Cloud sync can change reparse metadata without changing a live log's bytes.
// Cleanup still uses the complete version above before deleting any retained file.
const appendVersion = (value: Stamp): string =>
  [identity(value), value.size, value.mtimeNs, value.nlink].join(':');
const regular = (value: Stamp): boolean =>
  value.isFile() && !value.isSymbolicLink() && value.nlink === 1n;
const object = (value: unknown): value is Record<string, unknown> =>
  value !== null && typeof value === 'object' && !Array.isArray(value);
const code = (value: unknown): value is string =>
  typeof value === 'string' && (reason(value) || protocolCodes.has(value));
const commandName = (value: unknown): value is string =>
  typeof value === 'string' && /^[a-zA-Z][a-zA-Z0-9:._-]{0,63}$/.test(value);
const publicVersion = (value: unknown): value is string =>
  typeof value === 'string' &&
  value.length <= 96 &&
  /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$/.test(value);
const boundedLimit = (
  value: number | undefined,
  fallback: number,
  minimum: number,
  maximum: number,
): number =>
  Number.isSafeInteger(value) ? Math.max(minimum, Math.min(value!, maximum)) : fallback;

function detailsOf(details: CommandDetails | undefined): Fields {
  const result: Fields = {};
  if (operation(details?.operation)) result.operation = details!.operation!;
  if (guid(details?.scriptId)) result.scriptId = details!.scriptId!;
  if (publicVersion(details?.version)) result.version = details.version;
  if (details?.channel === 'stable' || details?.channel === 'test')
    result.channel = details.channel;
  for (const key of [
    'runAtLogin',
    'runAsAdministrator',
    'launchManagerAtLogin',
    'launchManagerAsAdministrator',
    'startInTray',
  ] as const) {
    if (typeof details?.[key] === 'boolean' || (key === 'runAtLogin' && details?.[key] === null))
      result[key] = details![key]!;
  }
  return result;
}

/** Extract debug-relevant intent without retaining arbitrary request payloads. */
export function commandDetails(payload: unknown): CommandDetails {
  if (!object(payload)) return {};
  const selection = object(payload.selection)
    ? payload.selection
    : object(payload.currentSelection)
      ? payload.currentSelection
      : null;
  const preferences = object(payload.metadata)
    ? payload.metadata
    : object(payload.settings)
      ? payload.settings
      : payload;
  const details: Record<string, unknown> = {
    operation: payload.operation,
    scriptId: payload.scriptId ?? selection?.scriptId,
    version: payload.version,
    channel: payload.channel,
  };
  for (const key of [
    'runAtLogin',
    'runAsAdministrator',
    'launchManagerAtLogin',
    'launchManagerAsAdministrator',
    'startInTray',
  ])
    details[key] = preferences[key];
  return detailsOf(details as CommandDetails) as CommandDetails;
}

/** Only finite classifications and counts reach disk, never diagnostic-bearing payloads. */
function resultOf(value: unknown): Fields {
  if (value === null) return { result: 'cancelled' };
  if (value === false) return { result: 'declined' };
  if (
    typeof value === 'string' &&
    ['created', 'updated', 'unchanged', 'preserved', 'not-applicable', 'failed'].includes(value)
  )
    return { result: value };
  if (Array.isArray(value))
    return { result: 'completed', itemCount: Math.min(value.length, 100000) };
  if (!object(value)) return { result: 'completed' };
  const target = object(value.result) ? value.result : object(value.plan) ? value.plan : value;
  const result: Fields = { result: 'completed' };
  if (outcome(target.outcome)) {
    result.outcome = target.outcome as string;
    result.result =
      target.outcome === 'CancelledBeforeDispatch'
        ? 'cancelled'
        : (target.outcome as string).toLowerCase();
  }
  if (code(target.code)) result.code = target.code;
  if (code(target.reason)) result.code = target.reason;
  const state = target.phase ?? target.state;
  if (typeof state === 'string' && states.has(state)) result.state = state;
  if (state === 'error' || state === 'Failed' || state === 'Denied' || state === 'Expired')
    result.result = 'failed';
  if (
    target.allowed === false ||
    target.canTransfer === false ||
    target.canImport === false ||
    target.canMigrate === false
  )
    result.result = 'refused';
  if (target.error !== null && target.error !== undefined) result.result = 'failed';
  if (Array.isArray(value.added)) result.addedCount = Math.min(value.added.length, 100000);
  if (Array.isArray(value.issues)) result.issueCount = Math.min(value.issues.length, 100000);
  if (Number.isSafeInteger(value.duplicateCount) && (value.duplicateCount as number) >= 0)
    result.duplicateCount = Math.min(value.duplicateCount as number, 100000);
  if (guid(target.correlationId)) result.correlationId = target.correlationId as string;
  if (object(value.state) && value.state.error !== null && value.state.error !== undefined)
    result.result = 'failed';
  return result;
}

function errorOf(value: unknown): Fields {
  const result: Fields = { result: 'threw', errorName: 'Error' };
  if (!object(value)) return result;
  if (typeof value.name === 'string' && errorNames.has(value.name)) result.errorName = value.name;
  if (typeof value.code === 'string' && (systemCodes.has(value.code) || code(value.code)))
    result.errorCode = value.code;
  // The backend request number identifies an unknown dispatch without exposing its session token.
  if (typeof value.requestId === 'string' && /^\d{1,20}$/.test(value.requestId))
    result.backendRequestId = value.requestId;
  return result;
}

/** Bounded local command traces complement Activity. Failures never change command behavior. */
export class CommandLog implements CommandLogBoundary {
  readonly directory: string;
  private readonly sessionId = randomUUID();
  private readonly createdAt = new Date().toISOString();
  private readonly timestamp = this.createdAt.replace(/[-:.]/g, '');
  private readonly context = new AsyncLocalStorage<string>();
  private readonly maximumFileBytes: number;
  private readonly maximumFiles: number;
  private directoryIdentity: string | null = null;
  private active: {
    file: string;
    descriptor: number;
    identity: string;
    version: string;
    bytes: number;
  } | null = null;
  private failure: CommandLogStatus['failure'] = null;
  private index = 0;
  private closed = false;

  constructor(dataDirectory: string, options: CommandLogOptions = {}) {
    this.directory = path.join(dataDirectory, 'Logs');
    this.maximumFileBytes = boundedLimit(
      options.maximumFileBytes,
      2 * 1024 * 1024,
      4096,
      16 * 1024 * 1024,
    );
    this.maximumFiles = boundedLimit(options.maximumFiles, 5, 2, 10);
    this.safely(() => {
      if (!path.isAbsolute(dataDirectory) || dataDirectory.startsWith('\\\\'))
        throw new Error('unsafe-path');
      assertStoragePath(dataDirectory);
      if (!fs.lstatSync(dataDirectory).isDirectory()) throw new Error('unsafe-path');
      assertStoragePath(this.directory);
      try {
        fs.mkdirSync(this.directory, { mode: 0o700 });
      } catch (error) {
        if ((error as NodeJS.ErrnoException)?.code !== 'EEXIST') throw error;
      }
      const directory = fs.lstatSync(this.directory, { bigint: true });
      if (!directory.isDirectory() || directory.isSymbolicLink()) throw new Error('unsafe-path');
      this.directoryIdentity = identity(directory);
      this.openFile();
    });
  }

  status(): CommandLogStatus {
    return {
      available: !this.closed && this.failure === null && this.active !== null,
      directory: this.directory,
      failure: this.closed ? 'closed' : this.failure,
    };
  }

  async run<T>(
    source: CommandSource,
    command: string,
    work: () => T | Promise<T>,
    details?: CommandDetails,
  ): Promise<T> {
    const requestId = randomUUID();
    const parentRequestId = this.context.getStore() ?? null;
    const started = performance.now();
    let fields: Fields = {};
    this.safely(() => {
      fields = {
        source: sources.has(source) ? source : 'lifecycle',
        command: commandName(command) ? command : 'unknown',
        ...detailsOf(details),
      };
      this.write({ kind: 'command', ...fields, requestId, parentRequestId, phase: 'started' });
    });
    return this.context.run(requestId, async () => {
      try {
        const result = await work();
        this.safely(() =>
          this.write({
            kind: 'command',
            ...fields,
            requestId,
            parentRequestId,
            phase: 'completed',
            durationMs: Math.max(0, Math.round(performance.now() - started)),
            ...resultOf(result),
          }),
        );
        return result;
      } catch (error) {
        this.safely(() =>
          this.write({
            kind: 'command',
            ...fields,
            requestId,
            parentRequestId,
            phase: 'error',
            durationMs: Math.max(0, Math.round(performance.now() - started)),
            ...errorOf(error),
          }),
        );
        throw error;
      }
    });
  }

  close(): void {
    if (this.closed) return;
    this.safely(() => this.write({ kind: 'session-end' }));
    this.closeFile();
    this.closed = true;
  }

  private safely(work: () => void): void {
    if (this.closed || this.failure !== null) return;
    try {
      work();
    } catch (error) {
      this.failure =
        error instanceof Error &&
        (error.message === 'unsafe-path' || error.message === 'retention-limit')
          ? error.message
          : 'unavailable';
      this.closeFile();
    }
  }

  private closeFile(): void {
    const active = this.active;
    this.active = null;
    if (active) {
      try {
        fs.fsyncSync(active.descriptor);
      } catch {
        /* Logging never blocks shutdown. */
      }
      try {
        fs.closeSync(active.descriptor);
      } catch {
        /* Descriptor may already be unavailable. */
      }
    }
  }

  private assertDirectory(): void {
    assertStoragePath(this.directory);
    const directory = fs.lstatSync(this.directory, { bigint: true });
    if (
      !directory.isDirectory() ||
      directory.isSymbolicLink() ||
      identity(directory) !== this.directoryIdentity
    )
      throw new Error('unsafe-path');
  }

  private inspect(file: string): RetainedFile | null {
    let descriptor: number | null = null;
    try {
      const match = filePattern.exec(path.basename(file));
      if (!match || !guid(match[2])) return null;
      const before = fs.lstatSync(file, { bigint: true });
      if (!regular(before) || before.size < 1n || before.size > BigInt(this.maximumFileBytes))
        return null;
      descriptor = fs.openSync(file, fs.constants.O_RDONLY | (fs.constants.O_NOFOLLOW ?? 0));
      if (version(fs.fstatSync(descriptor, { bigint: true })) !== version(before)) return null;
      const contents = Buffer.alloc(Number(before.size));
      let offset = 0;
      while (offset < contents.length) {
        const read = fs.readSync(descriptor, contents, offset, contents.length - offset, offset);
        if (!read) return null;
        offset += read;
      }
      const lines = new TextDecoder('utf-8', { fatal: true }).decode(contents).split('\n');
      if (lines.at(-1) === '') lines.pop();
      const header = JSON.parse(lines.shift() ?? '') as Record<string, unknown>;
      if (
        !object(header) ||
        Object.keys(header).some(
          (key) =>
            ![
              'schemaVersion',
              'kind',
              'applicationId',
              'sessionId',
              'timestamp',
              'pid',
              'fileIndex',
            ].includes(key),
        ) ||
        header.schemaVersion !== 1 ||
        header.kind !== 'session' ||
        header.applicationId !== applicationId ||
        header.sessionId !== match[2] ||
        header.fileIndex !== Number(match[3]) ||
        !Number.isSafeInteger(header.pid) ||
        (header.pid as number) < 1 ||
        typeof header.timestamp !== 'string' ||
        header.timestamp.replace(/[-:.]/g, '') !== match[1]
      )
        return null;
      const known = new Set([
        'schemaVersion',
        'kind',
        'sessionId',
        'timestamp',
        'source',
        'command',
        'operation',
        'scriptId',
        'version',
        'channel',
        'runAtLogin',
        'runAsAdministrator',
        'launchManagerAtLogin',
        'launchManagerAsAdministrator',
        'startInTray',
        'requestId',
        'parentRequestId',
        'phase',
        'durationMs',
        'result',
        'outcome',
        'code',
        'state',
        'addedCount',
        'duplicateCount',
        'issueCount',
        'itemCount',
        'correlationId',
        'errorName',
        'errorCode',
        'backendRequestId',
      ]);
      for (const line of lines) {
        const record = JSON.parse(line) as Record<string, unknown>;
        if (
          !object(record) ||
          record.schemaVersion !== 1 ||
          record.sessionId !== match[2] ||
          !['command', 'session-end'].includes(record.kind as string) ||
          Object.keys(record).some((key) => !known.has(key))
        )
          return null;
      }
      const after = fs.fstatSync(descriptor, { bigint: true });
      const current = fs.lstatSync(file, { bigint: true });
      return regular(current) &&
        version(after) === version(before) &&
        version(current) === version(before)
        ? { file, stamp: current }
        : null;
    } catch {
      return null;
    } finally {
      if (descriptor !== null)
        try {
          fs.closeSync(descriptor);
        } catch {}
    }
  }

  private prune(): void {
    this.assertDirectory();
    const names = fs.readdirSync(this.directory);
    if (names.length > 256) throw new Error('retention-limit');
    const owned = names
      .filter((name) => filePattern.test(name))
      .sort()
      .map((name) => this.inspect(path.join(this.directory, name)))
      .filter((file): file is RetainedFile => file !== null);
    while (owned.length >= this.maximumFiles) {
      const oldest = owned.shift()!;
      this.assertDirectory();
      const current = this.inspect(oldest.file);
      if (!current || version(current.stamp) !== version(oldest.stamp))
        throw new Error('unsafe-path');
      fs.unlinkSync(oldest.file);
    }
  }

  private openFile(): void {
    this.prune();
    if (this.index > 999999) throw new Error('retention-limit');
    const fileIndex = this.index++;
    const file = path.join(
      this.directory,
      `commands-${this.timestamp}-${this.sessionId}-${String(fileIndex).padStart(6, '0')}.jsonl`,
    );
    const descriptor = fs.openSync(
      file,
      fs.constants.O_WRONLY |
        fs.constants.O_CREAT |
        fs.constants.O_EXCL |
        (fs.constants.O_NOFOLLOW ?? 0),
      0o600,
    );
    let opened: fs.BigIntStats;
    try {
      opened = fs.fstatSync(descriptor, { bigint: true });
    } catch (error) {
      try {
        fs.closeSync(descriptor);
      } catch {}
      throw error;
    }
    this.active = {
      file,
      descriptor,
      identity: identity(opened),
      version: appendVersion(opened),
      bytes: 0,
    };
    if (!regular(opened)) throw new Error('unsafe-path');
    this.append(
      Buffer.from(
        JSON.stringify({
          schemaVersion: 1,
          kind: 'session',
          applicationId,
          sessionId: this.sessionId,
          timestamp: this.createdAt,
          pid: process.pid,
          fileIndex,
        }) + '\n',
      ),
    );
  }

  private append(bytes: Buffer): void {
    this.assertDirectory();
    const active = this.active;
    if (!active) throw new Error('unavailable');
    const current = fs.lstatSync(active.file, { bigint: true });
    const opened = fs.fstatSync(active.descriptor, { bigint: true });
    if (
      !regular(current) ||
      !regular(opened) ||
      identity(current) !== active.identity ||
      identity(opened) !== active.identity ||
      current.size !== BigInt(active.bytes) ||
      opened.size !== BigInt(active.bytes) ||
      appendVersion(current) !== active.version ||
      appendVersion(opened) !== active.version
    )
      throw new Error('unsafe-path');
    let offset = 0;
    while (offset < bytes.length) {
      const written = fs.writeSync(active.descriptor, bytes, offset, bytes.length - offset);
      if (written <= 0) throw new Error('unavailable');
      offset += written;
    }
    active.bytes += bytes.length;
    const written = fs.fstatSync(active.descriptor, { bigint: true });
    if (
      !regular(written) ||
      identity(written) !== active.identity ||
      written.size !== BigInt(active.bytes)
    )
      throw new Error('unsafe-path');
    active.version = appendVersion(written);
  }

  private write(fields: Fields): void {
    const bytes = Buffer.from(
      JSON.stringify({
        schemaVersion: 1,
        sessionId: this.sessionId,
        timestamp: new Date().toISOString(),
        ...fields,
      }) + '\n',
    );
    if (bytes.length > 2048) throw new Error('unavailable');
    if (this.active && this.active.bytes + bytes.length > this.maximumFileBytes) {
      this.closeFile();
      this.openFile();
    }
    this.append(bytes);
  }
}
