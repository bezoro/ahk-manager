import fs from 'node:fs';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { zipSync } from 'fflate';
import { guid } from '../../shared/validation.js';
import { assertStoragePath, type DeploymentLayout } from '../storage/deployment.js';
import { parseStorageJson } from '../storage/json.js';
import type { CommandLogExport } from './command-log.js';

type IssueReason =
  | 'unavailable'
  | 'unreadable'
  | 'unsafe-or-changed'
  | 'invalid-format'
  | 'size-limit'
  | 'entry-limit'
  | 'read-budget'
  | 'sanitized-records';
export interface DiagnosticArchiveIssue {
  file: string;
  reason: IssueReason;
  count?: number;
}
export interface DiagnosticArchiveEntry {
  file: string;
  status: 'included' | 'missing' | 'skipped';
  sourceBytes?: number;
  bytes?: number;
  sha256?: string;
  redactions?: number;
}
export interface DiagnosticArchiveManifest {
  formatVersion: 1;
  applicationId: 'Bezoro.AhkManager.Electron';
  collectedAt: string;
  complete: boolean;
  scope: string[];
  exclusions: string[];
  retention: string[];
  limits: {
    maximumFileBytes: number;
    maximumTotalBytes: number;
    maximumFiles: number;
    maximumDirectories: number;
  };
  entries: DiagnosticArchiveEntry[];
  issues: DiagnosticArchiveIssue[];
  redactions: number;
}
export interface DiagnosticArchiveInput {
  deployment: Pick<DeploymentLayout, 'profileDirectory' | 'updatesDirectory'>;
  summary: string;
  commandLogs: CommandLogExport;
  runtime?: unknown;
}
export interface DiagnosticArchiveOptions {
  maximumFileBytes?: number;
  maximumTotalBytes?: number;
  maximumFiles?: number;
  maximumDirectories?: number;
}
const marker = '[REDACTED]';
const secretKey =
  /^(?:access[_-]?token|auth(?:entication)?[_-]?token|refresh[_-]?token|session[_-]?token|token|api[_-]?key|private[_-]?key|credentials?|authorization|proxy[_-]?authorization|cookie|set[_-]?cookie|password|passwd|secret|client[_-]?secret|nonce|script[_-]?contents?|source[_-]?text|raw[_-]?script|request[_-]?body|response[_-]?body)$/i;
const secretQuery =
  /^(?:access_token|auth_token|refresh_token|token|api[_-]?key|authorization|password|secret|client_secret|nonce|code|key|sig|signature|x-amz-credential|x-amz-signature|x-amz-security-token|x-goog-credential|x-goog-signature)$/i;
const updateNames = new Set([
  'plan.json',
  'ready.json',
  'commit.json',
  'cancel.json',
  'outcome.json',
  'helper-refusal.json',
  'registry-before.json',
  'backup-manifest.json',
  'descriptor-before.json',
  'helper.log',
  'host-stderr.log',
]);
const identity = (value: fs.BigIntStats): string =>
  [value.dev, value.ino, value.birthtimeNs].join(':');
const version = (value: fs.BigIntStats): string =>
  [identity(value), value.size, value.mtimeNs, value.ctimeNs, value.nlink, value.mode].join(':');
const regular = (value: fs.BigIntStats): boolean =>
  value.isFile() && !value.isSymbolicLink() && value.nlink === 1n;
const missing = (error: unknown): boolean => (error as NodeJS.ErrnoException)?.code === 'ENOENT';
const clamp = (
  value: number | undefined,
  fallback: number,
  minimum: number,
  maximum: number,
): number =>
  Number.isSafeInteger(value) ? Math.max(minimum, Math.min(value!, maximum)) : fallback;

/** Keep error details and paths, while removing recognized credential and script-content fields. */
export function redactDiagnosticText(
  contents: string,
  structured = false,
): { contents: string; redactions: number } {
  let redactions = 0;
  const replace = (value: string): string => {
    if (value === marker) return value;
    redactions++;
    return marker;
  };
  const text = (value: string): string =>
    value
      .replace(/https?:\/\/[^\s<>"']+/gi, (match) => {
        try {
          const url = new URL(match);
          let changed = false;
          if (url.username || url.password) {
            url.username = '';
            url.password = '';
            redactions++;
            changed = true;
          }
          for (const key of [...url.searchParams.keys()])
            if (secretQuery.test(key)) {
              const current = url.searchParams.get(key);
              if (current !== marker) {
                url.searchParams.set(key, replace(current ?? ''));
                changed = true;
              }
            }
          return changed ? url.toString() : match;
        } catch {
          return match;
        }
      })
      .replace(/\b(?:gh[pousr]_[A-Za-z0-9_]{15,}|github_pat_[A-Za-z0-9_]{15,})\b/g, (value) =>
        replace(value),
      )
      .replace(
        /\b(Bearer|Basic)\s+[A-Za-z0-9._~+/=-]+/gi,
        (_match, scheme: string) => scheme + ' ' + replace('credential'),
      )
      .replace(
        /\b((?:authorization|proxy-authorization|cookie|set-cookie)\s*:\s*)[^\r\n]+/gi,
        (_match, prefix: string) => prefix + replace('credential'),
      )
      .replace(
        /("(?:access[_-]?token|auth(?:entication)?[_-]?token|refresh[_-]?token|session[_-]?token|token|api[_-]?key|password|passwd|secret|client[_-]?secret|nonce|authorization|cookie)"\s*:\s*)(?:"(?:\\.|[^"\\])*"|[^,}\]\r\n]+)/gi,
        (_match, prefix: string) => prefix + JSON.stringify(replace('credential')),
      )
      .replace(
        /\b((?:access[_-]?token|auth[_-]?token|refresh[_-]?token|token|api[_-]?key|password|passwd|secret|client[_-]?secret|nonce)\s*[:=]\s*)(?:"[^"\r\n]*"|'[^'\r\n]*'|[^\s,;&\r\n]+)/gi,
        (_match, prefix: string) => prefix + replace('credential'),
      )
      .replace(
        /(--(?:access-token|auth-token|token|api[_-]?key|password|secret|nonce)(?:=|\s+))(?:"[^"\r\n]*"|'[^'\r\n]*'|[^\s\r\n]+)/gi,
        (_match, prefix: string) => prefix + replace('credential'),
      );
  const visit = (value: unknown, depth = 0): unknown => {
    if (depth > 64) throw new Error('Diagnostic JSON exceeds the supported nesting depth.');
    if (typeof value === 'string') return text(value);
    if (Array.isArray(value)) return value.map((item) => visit(item, depth + 1));
    if (value !== null && typeof value === 'object') {
      const result: Record<string, unknown> = Object.create(null);
      for (const [key, child] of Object.entries(value))
        result[key] = secretKey.test(key) ? replace(String(child)) : visit(child, depth + 1);
      return result;
    }
    return value;
  };
  if (!structured) return { contents: text(contents), redactions };
  const value: unknown = parseStorageJson(contents);
  const sanitized = visit(value);
  return {
    contents: redactions ? JSON.stringify(sanitized, null, 2) + '\n' : contents,
    redactions,
  };
}

/** Collects only known diagnostic stores. Every discovered read failure is recorded in the ZIP. */
export function createDiagnosticArchive(
  input: DiagnosticArchiveInput,
  options: DiagnosticArchiveOptions = {},
): { bytes: Buffer; complete: boolean; manifest: DiagnosticArchiveManifest } {
  const limits = {
    maximumFileBytes: clamp(options.maximumFileBytes, 8 * 1024 * 1024, 1024, 8 * 1024 * 1024),
    maximumTotalBytes: clamp(options.maximumTotalBytes, 32 * 1024 * 1024, 1024, 32 * 1024 * 1024),
    maximumFiles: clamp(options.maximumFiles, 1024, 1, 1024),
    maximumDirectories: clamp(options.maximumDirectories, 256, 1, 256),
  };
  const manifest: DiagnosticArchiveManifest = {
    formatVersion: 1,
    applicationId: 'Bezoro.AhkManager.Electron',
    collectedAt: new Date().toISOString(),
    complete: true,
    scope: [
      'All available retained Manager command records, without a 200-record excerpt limit.',
      'Activity history and recovery journal, startup-continuation evidence, and update-operation diagnostic files.',
      'Selected current runtime state, including backend/update errors and startup issue details when supplied.',
      'The summary is a bounded convenience. Evidence files contain their full readable records. No live Task Scheduler XML is collected.',
    ],
    exclusions: [
      'Scripts, executable packages, installers, Chromium storage, profile settings, credential stores, and unrelated files.',
      'Optional command-line renderer diagnostics and screenshots outside the Manager data stores are not collected.',
      'Recognized credential values and script-content fields are redacted. Local paths and account names are retained for troubleshooting.',
      'Unsafe, changed, malformed, oversized or unreadable files are listed as collection issues, never silently omitted.',
    ],
    retention: [
      'Command logs normally retain up to five files of 2 MiB each.',
      'Activity normally retains its active file and three rotations of approximately 1 MiB each.',
      'Native updater helper logs are bounded to 32 KiB. New helpers explicitly mark message or file truncation.',
      'Previously rotated or deleted records cannot be recovered by this export.',
    ],
    limits,
    entries: [],
    issues: [],
    redactions: 0,
  };
  const files: Record<string, Uint8Array> = Object.create(null);
  let readBytes = 0;
  let storedBytes = 0;
  let fileCount = 0;
  let directoryCount = 0;
  const issue = (file: string, reason: IssueReason, count?: number): void => {
    manifest.issues.push({ file, reason, ...(count === undefined ? {} : { count }) });
  };
  const add = (
    file: string,
    source: string,
    structured: boolean,
    jsonLines = false,
    alreadyRead = false,
    sourceByteLength?: number,
  ): void => {
    const sourceBytes = sourceByteLength ?? Buffer.byteLength(source, 'utf8');
    if (++fileCount > limits.maximumFiles) {
      manifest.entries.push({ file, status: 'skipped', sourceBytes });
      issue(file, 'entry-limit');
      return;
    }
    if (sourceBytes > limits.maximumFileBytes) {
      manifest.entries.push({ file, status: 'skipped', sourceBytes });
      issue(file, 'size-limit');
      return;
    }
    if (!alreadyRead && readBytes + sourceBytes > limits.maximumTotalBytes) {
      manifest.entries.push({ file, status: 'skipped', sourceBytes });
      issue(file, 'read-budget');
      return;
    }
    if (!alreadyRead) readBytes += sourceBytes;
    try {
      let redactions = 0;
      let contents: string;
      if (jsonLines) {
        const lines = source.split('\n');
        const recovered: string[] = [];
        let invalidRecords = 0;
        for (const [index, line] of lines.entries()) {
          if (!line && index === lines.length - 1) continue;
          try {
            if (!line) throw new Error('Invalid empty diagnostic record.');
            const result = redactDiagnosticText(line, true);
            redactions += result.redactions;
            recovered.push(result.redactions ? JSON.stringify(JSON.parse(result.contents)) : line);
          } catch {
            invalidRecords++;
          }
        }
        if (invalidRecords && !recovered.length)
          throw new Error('Diagnostic records could not be recovered.');
        if (invalidRecords) issue(file, 'invalid-format', invalidRecords);
        contents = recovered.join('\n') + (recovered.length && source.endsWith('\n') ? '\n' : '');
      } else {
        const result = redactDiagnosticText(source, structured);
        contents = result.contents;
        redactions = result.redactions;
      }
      const bytes = Buffer.from(contents, 'utf8');
      if (bytes.length > limits.maximumFileBytes) {
        manifest.entries.push({ file, status: 'skipped', sourceBytes });
        issue(file, 'size-limit');
        return;
      }
      if (storedBytes + bytes.length > limits.maximumTotalBytes) {
        manifest.entries.push({ file, status: 'skipped', sourceBytes });
        issue(file, 'read-budget');
        return;
      }
      files[file] = bytes;
      storedBytes += bytes.length;
      manifest.redactions += redactions;
      manifest.entries.push({
        file,
        status: 'included',
        sourceBytes,
        bytes: bytes.length,
        sha256: createHash('sha256').update(bytes).digest('hex'),
        redactions,
      });
    } catch {
      manifest.entries.push({ file, status: 'skipped', sourceBytes });
      issue(file, 'invalid-format');
    }
  };
  const read = (source: string, file: string, format: 'json' | 'jsonl' | 'text'): void => {
    let descriptor: number | null = null;
    let found = false;
    try {
      assertStoragePath(source);
      const before = fs.lstatSync(source, { bigint: true });
      found = true;
      if (!regular(before)) {
        issue(file, 'unsafe-or-changed');
        manifest.entries.push({ file, status: 'skipped' });
        return;
      }
      if (before.size > BigInt(limits.maximumFileBytes)) {
        issue(file, 'size-limit');
        manifest.entries.push({ file, status: 'skipped', sourceBytes: Number(before.size) });
        return;
      }
      if (readBytes + Number(before.size) > limits.maximumTotalBytes) {
        issue(file, 'read-budget');
        manifest.entries.push({ file, status: 'skipped', sourceBytes: Number(before.size) });
        return;
      }
      if (fileCount >= limits.maximumFiles) {
        issue(file, 'entry-limit');
        manifest.entries.push({ file, status: 'skipped' });
        return;
      }
      descriptor = fs.openSync(source, fs.constants.O_RDONLY | (fs.constants.O_NOFOLLOW ?? 0));
      if (version(fs.fstatSync(descriptor, { bigint: true })) !== version(before))
        throw new Error('changed');
      readBytes += Number(before.size);
      const bytes = Buffer.alloc(Number(before.size));
      let offset = 0;
      while (offset < bytes.length) {
        const count = fs.readSync(descriptor, bytes, offset, bytes.length - offset, offset);
        if (!count) throw new Error('changed');
        offset += count;
      }
      const current = fs.lstatSync(source, { bigint: true });
      assertStoragePath(source);
      if (
        !regular(current) ||
        version(fs.fstatSync(descriptor, { bigint: true })) !== version(before) ||
        version(current) !== version(before)
      )
        throw new Error('changed');
      let contents: string;
      try {
        contents = new TextDecoder('utf-8', { fatal: true }).decode(bytes);
      } catch (error) {
        if (format === 'json') throw error;
        const recovered: string[] = [];
        let invalidLines = 0;
        for (let start = 0; start < bytes.length;) {
          const newline = bytes.indexOf(10, start);
          const end = newline < 0 ? bytes.length : newline + 1;
          try {
            recovered.push(
              new TextDecoder('utf-8', { fatal: true }).decode(bytes.subarray(start, end)),
            );
          } catch {
            invalidLines++;
          }
          start = end;
        }
        issue(file, 'invalid-format', invalidLines);
        contents = recovered.join('');
      }
      add(file, contents, format === 'json', format === 'jsonl', true, bytes.length);
    } catch (error) {
      if (missing(error) && !found) manifest.entries.push({ file, status: 'missing' });
      else {
        manifest.entries.push({ file, status: 'skipped' });
        issue(
          file,
          found && (missing(error) || (error instanceof Error && error.message === 'changed'))
            ? 'unsafe-or-changed'
            : 'unreadable',
        );
      }
    } finally {
      if (descriptor !== null)
        try {
          fs.closeSync(descriptor);
        } catch {
          /* Read-only collection. */
        }
    }
  };
  const list = (directory: string, name: string): string[] => {
    if (++directoryCount > limits.maximumDirectories) {
      issue(name, 'entry-limit');
      return [];
    }
    try {
      assertStoragePath(directory);
      const before = fs.lstatSync(directory, { bigint: true });
      if (!before.isDirectory() || before.isSymbolicLink()) {
        issue(name, 'unsafe-or-changed');
        return [];
      }
      const names = fs.readdirSync(directory).sort();
      assertStoragePath(directory);
      if (identity(fs.lstatSync(directory, { bigint: true })) !== identity(before)) {
        issue(name, 'unsafe-or-changed');
        return [];
      }
      if (names.length > limits.maximumFiles) {
        issue(name, 'entry-limit', names.length - limits.maximumFiles);
        return names.slice(0, limits.maximumFiles);
      }
      return names;
    } catch (error) {
      if (!missing(error)) issue(name, 'unreadable');
      return [];
    }
  };
  add('summary.txt', input.summary, false);
  if (input.runtime !== undefined) {
    try {
      const serialized = JSON.stringify(input.runtime);
      if (typeof serialized !== 'string') throw new Error('Runtime state could not be serialized.');
      add('runtime.json', serialized, true);
    } catch {
      manifest.entries.push({ file: 'runtime.json', status: 'skipped' });
      issue('runtime.json', 'invalid-format');
    }
  }
  for (const entry of input.commandLogs.files) {
    if (!/^commands-\d{8}T\d{9}Z-[0-9a-f-]{36}-\d{6}\.jsonl$/.test(entry.name)) {
      issue('Logs', 'invalid-format');
      continue;
    }
    add('Logs/' + entry.name, entry.contents, false, true);
  }
  for (const entry of input.commandLogs.issues)
    issue(
      entry.file === 'Logs' ? 'Logs' : 'Logs/' + entry.file,
      entry.reason as IssueReason,
      entry.count,
    );
  const profile = input.deployment.profileDirectory;
  for (let index = 0; index <= 10; index++) {
    const name = 'activity.jsonl' + (index ? '.' + index : '');
    read(path.join(profile, name), 'Profile/' + name, 'jsonl');
  }
  read(path.join(profile, 'operations.json'), 'Profile/operations.json', 'json');
  const startup = path.join(profile, 'startup-continuations');
  for (const name of list(startup, 'Profile/startup-continuations')) {
    if (!/\.json$/i.test(name)) continue;
    if (name.length > 255 || /[\x00-\x1f\\/:]/.test(name)) {
      issue('Profile/startup-continuations', 'unsafe-or-changed');
      continue;
    }
    // Core examines every *.json entry. A non-GUID filename can itself block startup.
    read(path.join(startup, name), 'Profile/startup-continuations/' + name, 'json');
  }
  const quarantine = path.join(startup, 'quarantine');
  for (const operation of list(quarantine, 'Profile/startup-continuations/quarantine')) {
    if (!guid(operation)) continue;
    const source = path.join(quarantine, operation);
    const prefix = 'Profile/startup-continuations/quarantine/' + operation;
    for (const name of list(source, prefix))
      if (
        name === 'manifest.json' ||
        name === 'completed.json' ||
        /^\d{4}-[0-9a-f]{64}\.json$/i.test(name)
      )
        read(path.join(source, name), prefix + '/' + name, 'json');
  }
  const operations = path.join(input.deployment.updatesDirectory, 'operations');
  for (const operation of list(operations, 'Updates/operations')) {
    if (!guid(operation) && !/^[a-f0-9-]{36}$/.test(operation)) continue;
    const source = path.join(operations, operation);
    const prefix = 'Updates/operations/' + operation;
    if (!guid(operation)) issue(prefix, 'invalid-format');
    for (const name of list(source, prefix))
      if (updateNames.has(name))
        read(
          path.join(source, name),
          prefix + '/' + name,
          name.endsWith('.json') ? 'json' : 'text',
        );
  }
  manifest.complete = manifest.issues.length === 0;
  files['manifest.json'] = Buffer.from(JSON.stringify(manifest, null, 2) + '\n');
  files['README.txt'] = Buffer.from(
    'AHK Manager diagnostic archive\n\n' +
      (manifest.complete
        ? 'All readable evidence within the declared scope was collected.\n'
        : 'INCOMPLETE COLLECTION: see every issue in manifest.json before drawing conclusions.\n') +
      'Times are UTC. Local paths and account names may be included. Recognized credentials and script-content fields are redacted.\nThe summary is an excerpt; evidence files contain full available records. Previously rotated or deleted history cannot be recovered.\nNo live Task Scheduler XML or Windows reboot/logon behavior is captured by this archive.\n',
  );
  return {
    bytes: Buffer.from(zipSync(files, { level: 6 })),
    complete: manifest.complete,
    manifest,
  };
}
