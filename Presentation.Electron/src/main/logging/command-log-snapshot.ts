import fs from 'node:fs';
import path from 'node:path';
import { guid, operation, outcome, parseStrictJson, reason } from '../../shared/validation.js';
import { parseUpdateVersion } from '../../shared/update-versions.js';
import { assertStoragePath } from '../storage/deployment.js';

export type DiagnosticLogMode = 'recent' | 'errors';
export interface CommandLogSnapshot {
  records: Readonly<Record<string, string | number | boolean | null>>[];
  omittedRecords: number;
  skippedFiles: number;
}
export type CommandLogExportIssueReason =
  | 'unavailable'
  | 'unsafe-or-changed'
  | 'invalid-format'
  | 'sanitized-records'
  | 'size-limit'
  | 'entry-limit'
  | 'read-budget';
export interface CommandLogExportIssue {
  file: string;
  reason: CommandLogExportIssueReason;
  count?: number;
}
export interface CommandLogExport {
  files: { name: string; contents: string }[];
  issues: CommandLogExportIssue[];
}
type Fields = Record<string, string | number | boolean | null>;
type Stamp = fs.BigIntStats;
const applicationId = 'Bezoro.AhkManager.Electron';
const filePattern = /^commands-(\d{8}T\d{9}Z)-([0-9a-f-]{36})-(\d{6})\.jsonl$/;
const maximumRecords = 200;
const maximumFileBytes = 2 * 1024 * 1024;
const maximumExportBytes = 32 * 1024 * 1024;
const identity = (value: Stamp): string => [value.dev, value.ino, value.birthtimeNs].join(':');
const version = (value: Stamp): string =>
  [identity(value), value.size, value.mtimeNs, value.ctimeNs, value.nlink, value.mode].join(':');
const regular = (value: Stamp): boolean =>
  value.isFile() && !value.isSymbolicLink() && value.nlink === 1n;
const object = (value: unknown): value is Record<string, unknown> =>
  value !== null && typeof value === 'object' && !Array.isArray(value);
const timestamp = (value: unknown): value is string => {
  if (typeof value !== 'string' || !/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$/.test(value))
    return false;
  const parsed = Date.parse(value);
  return Number.isFinite(parsed) && new Date(parsed).toISOString() === value;
};

// Log files are editable local evidence. A grammar alone would also accept secrets as command names.
// Unknown future command names retain their result and correlation IDs under a fixed label.
const commandNames = new Set([
  'unknown',
  'connect',
  'getState',
  'getTheme',
  'getUpdateState',
  'openLogs',
  'copyDiagnostics',
  'exportDiagnostics',
  'storage:get',
  'storage:choose',
  'storage:open',
  'storage:restore-default',
  'storage:restart',
  'storage:cancel',
  'checkUpdates',
  'installUpdate',
  'setUpdateChannel',
  'requestAdministration',
  'refresh',
  'addFiles',
  'addFolder',
  'removeScript',
  'locateScript',
  'deleteScriptFile',
  'saveMetadata',
  'saveCollection',
  'removeCollection',
  'operate',
  'saveSettings',
  'pickInterpreter',
  'pickEditor',
  'getActivity',
  'previewImport',
  'commitImport',
  'migrateProfile',
  'transferStartup',
  'restoreBackup',
  'recoverPending',
  'retryBackend',
  'copyText',
  'updateSelection',
  'documentReady',
  'administrationProvider',
  'administrationPreflightReply',
  'hello',
  'initializeProfile',
  'preparePortableDataDirectory',
  'load',
  'snapshot',
  'remove',
  'relink',
  'prepareRecycle',
  'authorizeRecycle',
  'completeRecycle',
  'beginAdministration',
  'administrationStatus',
  'commitAdministration',
  'cancelAdministration',
  'beginStartupContinuation',
  'startupContinuation',
  'adoptStartupContinuation',
  'startupAdoptionStatus',
  'initializeStartup',
  'prepare',
  'perform',
  'runLogin',
  'activity',
  'import',
  'previewMigration',
  'migrate',
  'shutdown',
  'previewStartupTransfer',
  'initialize',
  'check',
  'start',
  'startBackend',
  'activate',
  'secondInstance',
  'closeWindow',
  'uncaughtException',
  'repairSearchShortcut',
  'confirm',
  'checkAdoption',
  'continueStartup',
  'toggleWindow',
  'preferences',
  'quit',
  'stopScriptsAndQuit',
  'cancelStopExit',
]);
const shortcutPhases = new Set([
  'validation',
  'launcher',
  'directory',
  'existing',
  'stage-create',
  'native-write',
  'stage-verify',
  'rename',
  'backup-verify',
  'publish',
  'cleanup',
  'restore',
]);
const knownCommand = (value: unknown): string => {
  if (typeof value !== 'string') return 'unknown';
  if (commandNames.has(value)) return value;
  if (value.startsWith('dispatch.') && commandNames.has(value.slice(9))) return value;
  if (value.startsWith('repairSearchShortcut.') && shortcutPhases.has(value.slice(21)))
    return value;
  if (/^pick\.(scripts|locate|folder|interpreter|editor|import|backup|migration)$/.test(value))
    return value;
  return 'unknown';
};
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
const results = new Set([
  'completed',
  'cancelled',
  'declined',
  'created',
  'updated',
  'unchanged',
  'preserved',
  'not-applicable',
  'failed',
  'refused',
  'partial',
  'unknown',
  'pending',
  'accepted',
  'confirmed',
  'threw',
]);
const failureResults = new Set(['threw', 'failed', 'refused', 'partial', 'unknown']);
const failureStates = new Set(['error', 'Failed', 'Denied', 'Expired', 'Unknown']);
const finiteString = (value: unknown, allowed: Set<string>): value is string =>
  typeof value === 'string' && allowed.has(value);
const nonnegative = (value: unknown, maximum: number): value is number =>
  typeof value === 'number' && Number.isSafeInteger(value) && value >= 0 && value <= maximum;

/** Rebuild every field instead of trusting a log's key names or serializing its raw records. */
function sanitize(value: unknown, sessionId: string): Fields | null {
  if (
    !object(value) ||
    value.schemaVersion !== 1 ||
    value.kind !== 'command' ||
    value.sessionId !== sessionId ||
    !timestamp(value.timestamp) ||
    !guid(value.requestId) ||
    (value.parentRequestId !== null && !guid(value.parentRequestId)) ||
    !finiteString(value.source, sources) ||
    !['started', 'completed', 'error'].includes(value.phase as string)
  )
    return null;
  const result: Fields = {
    schemaVersion: 1,
    kind: 'command',
    sessionId,
    timestamp: value.timestamp,
    source: value.source,
    command: knownCommand(value.command),
    requestId: value.requestId as string,
    parentRequestId: value.parentRequestId as string | null,
    phase: value.phase as string,
  };
  for (const key of ['scriptId', 'correlationId'] as const)
    if (guid(value[key])) result[key] = value[key] as string;
  if (operation(value.operation)) result.operation = value.operation as string;
  if (parseUpdateVersion(value.version)) result.version = value.version as string;
  if (value.channel === 'stable' || value.channel === 'test') result.channel = value.channel;
  for (const key of [
    'runAtLogin',
    'runAsAdministrator',
    'launchManagerAtLogin',
    'launchManagerAsAdministrator',
    'startInTray',
    'retrying',
  ] as const)
    if (typeof value[key] === 'boolean' || (key === 'runAtLogin' && value[key] === null))
      result[key] = value[key] as boolean | null;
  if (nonnegative(value.attempt, 32)) result.attempt = value.attempt;
  if (nonnegative(value.durationMs, Number.MAX_SAFE_INTEGER)) result.durationMs = value.durationMs;
  for (const key of ['addedCount', 'duplicateCount', 'issueCount', 'itemCount'] as const)
    if (nonnegative(value[key], 100000)) result[key] = value[key];
  if (finiteString(value.result, results)) result.result = value.result;
  if (outcome(value.outcome)) result.outcome = value.outcome as string;
  if (reason(value.code) || finiteString(value.code, protocolCodes))
    result.code = value.code as string;
  if (finiteString(value.state, states)) result.state = value.state;
  if (finiteString(value.errorName, errorNames)) result.errorName = value.errorName;
  if (
    reason(value.errorCode) ||
    finiteString(value.errorCode, protocolCodes) ||
    finiteString(value.errorCode, systemCodes)
  )
    result.errorCode = value.errorCode as string;
  if (typeof value.backendRequestId === 'string' && /^\d{1,20}$/.test(value.backendRequestId))
    result.backendRequestId = value.backendRequestId;
  return result;
}

interface InspectedLog {
  records: Fields[];
  contents: string;
  issues: CommandLogExportIssue[];
}

function readFile(
  file: string,
  byteLimit: number,
  complete = false,
  remainingBudget = maximumExportBytes,
  accountRead: (bytes: number) => void = () => {},
): InspectedLog | CommandLogExportIssueReason {
  let descriptor: number | null = null;
  try {
    const match = filePattern.exec(path.basename(file));
    if (!match || !guid(match[2])) return 'invalid-format';
    const before = fs.lstatSync(file, { bigint: true });
    if (!regular(before)) return 'unsafe-or-changed';
    if (before.size < 1n) return 'invalid-format';
    if (before.size > BigInt(byteLimit)) return 'size-limit';
    if (before.size > BigInt(remainingBudget)) return 'read-budget';
    descriptor = fs.openSync(file, fs.constants.O_RDONLY | (fs.constants.O_NOFOLLOW ?? 0));
    if (version(fs.fstatSync(descriptor, { bigint: true })) !== version(before))
      return 'unsafe-or-changed';
    accountRead(Number(before.size));
    const buffer = Buffer.alloc(Number(before.size));
    let offset = 0;
    while (offset < buffer.length) {
      const count = fs.readSync(descriptor, buffer, offset, buffer.length - offset, offset);
      if (!count) return 'unsafe-or-changed';
      offset += count;
    }
    let contents: string;
    try {
      contents = new TextDecoder('utf-8', { fatal: true }).decode(buffer);
    } catch {
      return 'invalid-format';
    }
    if (!complete && !contents.endsWith('\n')) return 'invalid-format';
    const lines = contents.trimEnd().split('\n');
    let header: unknown;
    try {
      header = parseStrictJson(lines.shift() ?? '');
    } catch {
      return 'invalid-format';
    }
    if (
      !object(header) ||
      header.schemaVersion !== 1 ||
      header.kind !== 'session' ||
      header.applicationId !== applicationId ||
      header.sessionId !== match[2] ||
      header.fileIndex !== Number(match[3]) ||
      !nonnegative(header.pid, 2147483647) ||
      header.pid === 0 ||
      !timestamp(header.timestamp) ||
      header.timestamp.replace(/[-:.]/g, '') !== match[1] ||
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
      )
    )
      return 'invalid-format';
    const records: Fields[] = [];
    const exported: string[] = [JSON.stringify(header)];
    let sanitizedRecords = 0;
    let invalidRecords = 0;
    const sameFields = (original: Record<string, unknown>, selected: Fields): boolean =>
      Object.keys(original).length === Object.keys(selected).length &&
      Object.keys(original).every((key) => original[key] === selected[key]);
    for (const line of lines) {
      let value: unknown;
      try {
        if (Buffer.byteLength(line, 'utf8') > 2048) throw new Error('Invalid record size.');
        value = parseStrictJson(line);
      } catch {
        if (!complete) return 'invalid-format';
        invalidRecords++;
        continue;
      }
      const record = sanitize(value, match[2]!);
      if (record) {
        records.push(record);
        if (complete) {
          exported.push(JSON.stringify(record));
          if (!sameFields(value as Record<string, unknown>, record)) sanitizedRecords++;
        }
      } else if (complete) {
        if (
          object(value) &&
          value.schemaVersion === 1 &&
          value.kind === 'session-end' &&
          value.sessionId === match[2] &&
          timestamp(value.timestamp)
        ) {
          const ending: Fields = {
            schemaVersion: 1,
            kind: 'session-end',
            sessionId: match[2]!,
            timestamp: value.timestamp,
          };
          exported.push(JSON.stringify(ending));
          if (!sameFields(value, ending)) sanitizedRecords++;
        } else invalidRecords++;
      }
    }
    const after = fs.fstatSync(descriptor, { bigint: true });
    const current = fs.lstatSync(file, { bigint: true });
    if (
      !regular(current) ||
      version(after) !== version(before) ||
      version(current) !== version(before)
    )
      return 'unsafe-or-changed';
    const issues: CommandLogExportIssue[] = [];
    const name = path.basename(file);
    if (sanitizedRecords)
      issues.push({ file: name, reason: 'sanitized-records', count: sanitizedRecords });
    if (invalidRecords || !contents.endsWith('\n'))
      issues.push({ file: name, reason: 'invalid-format', count: invalidRecords });
    return {
      records,
      // Valid current logs are copied exactly, including headers, ordering and session endings.
      contents: issues.length ? exported.join('\n') + '\n' : contents,
      issues,
    };
  } catch {
    return 'unavailable';
  } finally {
    if (descriptor !== null)
      try {
        fs.closeSync(descriptor);
      } catch {
        /* No command behavior changes. */
      }
  }
}

function assertDirectoryIdentity(directory: string, expected: string | null): void {
  assertStoragePath(directory);
  const current = fs.lstatSync(directory, { bigint: true });
  if (!current.isDirectory() || current.isSymbolicLink() || identity(current) !== expected)
    throw new Error('Unsafe diagnostic log directory.');
}

/** Every available retained command log is considered; exclusions are explicit in the archive. */
export function readCommandLogFiles(
  directory: string,
  directoryIdentity: string | null,
  fileByteLimit: number,
): CommandLogExport {
  const files: CommandLogExport['files'] = [];
  const issues: CommandLogExportIssue[] = [];
  try {
    assertDirectoryIdentity(directory, directoryIdentity);
    const entries = fs.readdirSync(directory);
    if (entries.length > 256)
      return { files, issues: [{ file: 'Logs', reason: 'entry-limit', count: entries.length }] };
    let remainingBudget = maximumExportBytes;
    // Prefer the newest evidence if an abnormal directory exceeds the explicit total read budget.
    for (const name of entries
      .filter((entry) => filePattern.test(entry))
      .sort()
      .reverse()) {
      assertDirectoryIdentity(directory, directoryIdentity);
      const read = readFile(
        path.join(directory, name),
        Math.min(fileByteLimit, maximumFileBytes),
        true,
        remainingBudget,
        (bytes) => {
          remainingBudget -= bytes;
        },
      );
      if (typeof read === 'string') issues.push({ file: name, reason: read });
      else {
        files.push({ name, contents: read.contents });
        issues.push(...read.issues);
      }
    }
    assertDirectoryIdentity(directory, directoryIdentity);
    return { files: files.sort((a, b) => a.name.localeCompare(b.name)), issues };
  } catch {
    // Never return files read through a namespace whose identity cannot be confirmed afterwards.
    return { files: [], issues: [{ file: 'Logs', reason: 'unavailable' }] };
  }
}

/** Synchronous bounded snapshots avoid racing this process's synchronous appends. No files change. */
export function readCommandLogSnapshot(
  directory: string,
  directoryIdentity: string | null,
  limits: { maximumFileBytes: number; maximumFiles: number },
  mode: DiagnosticLogMode,
): CommandLogSnapshot {
  const unavailable = () =>
    new Error('Diagnostic logs could not be read. Check access to the data folder.');
  try {
    if (mode !== 'recent' && mode !== 'errors') throw unavailable();
    const checkDirectory = () => assertDirectoryIdentity(directory, directoryIdentity);
    checkDirectory();
    const entries = fs.readdirSync(directory);
    if (entries.length > 256) throw unavailable();
    const candidates = entries.filter((name) => filePattern.test(name)).sort();
    const selected = candidates.slice(-Math.min(limits.maximumFiles, 5));
    let skippedFiles = candidates.length - selected.length;
    let records: Fields[] = [];
    for (const name of selected) {
      checkDirectory();
      const read = readFile(
        path.join(directory, name),
        Math.min(limits.maximumFileBytes, maximumFileBytes),
      );
      if (typeof read === 'string') skippedFiles++;
      else records.push(...read.records);
    }
    checkDirectory();
    records.sort((a, b) => (a.timestamp as string).localeCompare(b.timestamp as string));
    if (mode === 'errors') {
      const wanted = new Set(
        records
          .filter(
            (record) =>
              record.phase === 'error' ||
              failureResults.has(record.result as string) ||
              failureStates.has(record.state as string),
          )
          .map((record) => record.requestId as string),
      );
      // Include each failed request's start and its ancestors without pulling in unrelated commands.
      const parents = new Map(
        records.map((record) => [record.requestId as string, record.parentRequestId]),
      );
      for (const requestId of wanted) {
        let parent = parents.get(requestId);
        for (let depth = 0; typeof parent === 'string' && depth < 32; depth++) {
          if (wanted.has(parent)) break;
          wanted.add(parent);
          parent = parents.get(parent);
        }
      }
      records = records.filter((record) => wanted.has(record.requestId as string));
    }
    return {
      records: records.slice(-maximumRecords),
      omittedRecords: Math.max(0, records.length - maximumRecords),
      skippedFiles,
    };
  } catch {
    throw unavailable();
  }
}
