import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { unzipSync } from 'fflate';
import {
  createDiagnosticArchive,
  redactDiagnosticText,
  type DiagnosticArchiveInput,
} from '../../src/main/logging/diagnostic-archive.js';

const operationId = '11111111-1111-1111-1111-111111111111';
const name = `commands-20261031T120000000Z-${operationId}-000000.jsonl`;
function fixture(t: { after(callback: () => void): void }): {
  root: string;
  input: DiagnosticArchiveInput;
  write(relative: string, contents: string): string;
} {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'ahk-archive-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  fs.mkdirSync(path.join(root, 'Profile'));
  fs.mkdirSync(path.join(root, 'Updates'));
  return {
    root,
    input: {
      deployment: {
        profileDirectory: path.join(root, 'Profile'),
        updatesDirectory: path.join(root, 'Updates'),
      },
      summary: 'AHK Manager diagnostic summary\n',
      commandLogs: { files: [], issues: [] },
    },
    write(relative, contents) {
      const file = path.join(root, relative);
      fs.mkdirSync(path.dirname(file), { recursive: true });
      fs.writeFileSync(file, contents);
      return file;
    },
  };
}
const entries = (bytes: Buffer): Record<string, string> =>
  Object.fromEntries(
    Object.entries(unzipSync(bytes)).map(([file, contents]) => [
      file,
      Buffer.from(contents).toString('utf8'),
    ]),
  );

test('the archive retains all available records beyond summary limits and every known diagnostic store', (t) => {
  const f = fixture(t);
  const commands =
    Array.from({ length: 650 }, (_, index) =>
      JSON.stringify({ kind: 'command', command: 'refresh', index }),
    ).join('\n') + '\n';
  f.input.commandLogs.files.push({ name, contents: commands });
  const activity =
    Array.from({ length: 350 }, (_, index) =>
      JSON.stringify({
        id: index,
        detail: 'Cannot open C:\\Users\\Bezor\\Scripts\\outside.ahk',
        code: 'AccessDenied',
      }),
    ).join('\n') + '\n';
  const originals = new Map<string, Buffer>();
  const record = (relative: string, contents: string): void => {
    const file = f.write(relative, contents);
    originals.set(file, Buffer.from(contents));
  };
  record('Profile/activity.jsonl', activity);
  record('Profile/activity.jsonl.1', '{"detail":"Earlier retained result"}\n');
  record(
    'Profile/operations.json',
    '{"Schema":1,"Operations":[{"LoginKey":"retained-login-key","ScriptPath":"D:\\\\Scripts\\\\outside.ahk"}]}',
  );
  record(
    `Profile/startup-continuations/${operationId}.json`,
    '{"Ticket":{"OperationId":"' +
      operationId +
      '","LoginKey":"local-logon","Items":[]},"Result":{"Detail":"StartupEntryConflict"}}',
  );
  record(`Profile/startup-continuations/quarantine/${operationId}/manifest.json`, '{"Files":[]}');
  record(`Profile/startup-continuations/quarantine/${operationId}/completed.json`, '{"Files":[]}');
  record(
    `Profile/startup-continuations/quarantine/${operationId}/0000-${'a'.repeat(64)}.json`,
    '{"Preserved":"invalid identity evidence"}',
  );
  for (const file of [
    'plan.json',
    'ready.json',
    'commit.json',
    'cancel.json',
    'outcome.json',
    'helper-refusal.json',
    'registry-before.json',
    'backup-manifest.json',
    'descriptor-before.json',
  ])
    record(
      `Updates/operations/${operationId}/${file}`,
      JSON.stringify({
        Version: '1.6.0',
        Detail: 'Useful admission failure detail',
        InstallerPath: 'C:\\Users\\Bezor\\Updates\\package.zip',
      }),
    );
  record(
    `Updates/operations/${operationId}/helper.log`,
    '2026-10-31T12:00:00Z Readiness rejected: task ownership differs\n',
  );
  f.write('Profile/settings.json', '{"password":"do-not-export-settings"}');
  f.write('Updates/github-token.bin', 'do-not-export-token-store');
  f.write('Scripts/private.ahk', 'do-not-export-script-content');
  f.write(
    `Updates/operations/${operationId}/unrelated.json`,
    '{"value":"do-not-export-unrelated"}',
  );
  const archive = createDiagnosticArchive(f.input);
  assert.equal(archive.complete, true);
  assert.deepEqual(archive.manifest.issues, []);
  const files = entries(archive.bytes);
  assert.equal(files['Logs/' + name], commands);
  assert.equal(files['Profile/activity.jsonl'], activity);
  assert.equal(files['Logs/' + name]!.trim().split('\n').length, 650);
  assert.equal(files['Profile/activity.jsonl']!.trim().split('\n').length, 350);
  assert.match(
    files[`Updates/operations/${operationId}/helper.log`]!,
    /Readiness rejected: task ownership differs/,
  );
  assert.match(files['Profile/operations.json']!, /retained-login-key/);
  assert.equal(
    Object.keys(files).some((file) => /settings|github-token|\.ahk$|unrelated/.test(file)),
    false,
  );
  for (const entry of archive.manifest.entries.filter((entry) => entry.status === 'included')) {
    assert.equal(entry.sha256, createHash('sha256').update(files[entry.file]!).digest('hex'));
    assert.equal(entry.bytes, Buffer.byteLength(files[entry.file]!));
  }
  for (const [file, original] of originals) assert.deepEqual(fs.readFileSync(file), original);
  assert.match(files['README.txt']!, /Previously rotated or deleted history cannot be recovered/);
  assert.match(files['manifest.json']!, /No live Task Scheduler XML/);
});

test('credential redaction retains actionable error details, local paths, versions and hashes', (t) => {
  const f = fixture(t);
  const secret = 'test-private-secret-value';
  f.write(
    'Profile/activity.jsonl',
    JSON.stringify({
      detail: `Error C:\\Users\\Bezor\\script.ahk with token=${secret}`,
      authToken: secret,
      password: secret,
      ScriptContents: 'MsgBox("private script")',
      version: '1.6.0',
      sha256: 'a'.repeat(64),
    }) + '\n',
  );
  f.write(
    `Updates/operations/${operationId}/plan.json`,
    JSON.stringify({
      InstallerPath: 'D:\\Portable\\package.zip',
      AuthToken: secret,
      nested: { Authorization: secret, nonce: secret },
      url: `https://username:${secret}@example.com/file.zip?token=${secret}&version=1.6.0&X-Amz-Signature=${secret}`,
    }),
  );
  f.write(
    `Updates/operations/${operationId}/helper.log`,
    `Access denied for C:\\Users\\Bezor\\Portable\nAuthorization: Bearer ${secret}\nCookie: session=${secret}\n--token=${secret}\nghp_${'x'.repeat(32)}\nhttps://example.com/file?sig=${secret}&release=1.6.0\n`,
  );
  const archive = createDiagnosticArchive(f.input);
  const files = entries(archive.bytes);
  const text = Object.values(files).join('\n');
  assert.equal(archive.complete, true);
  assert.ok(archive.manifest.redactions >= 10);
  assert.doesNotMatch(text, /test-private-secret-value|private script|username:|ghp_x+/);
  assert.match(text, /Users\\\\Bezor/);
  assert.match(text, /Access denied/);
  assert.match(text, /1\.6\.0/);
  assert.match(text, new RegExp('a'.repeat(64)));
  assert.match(text, /REDACTED/);
  for (const line of files['Profile/activity.jsonl']!.trim().split('\n'))
    assert.doesNotThrow(() => JSON.parse(line));
});

test('missing optional evidence is distinguished from malformed or unavailable evidence', (t) => {
  const f = fixture(t);
  const file = f.write('Profile/activity.jsonl', '{"truncated":\n');
  f.input.commandLogs.issues.push({ file: name, reason: 'sanitized-records', count: 2 });
  const archive = createDiagnosticArchive(f.input);
  assert.equal(archive.complete, false);
  assert.ok(
    archive.manifest.issues.some(
      (issue) => issue.file === 'Profile/activity.jsonl' && issue.reason === 'invalid-format',
    ),
  );
  assert.ok(
    archive.manifest.issues.some(
      (issue) => issue.reason === 'sanitized-records' && issue.count === 2,
    ),
  );
  assert.ok(
    archive.manifest.entries.some(
      (entry) => entry.file === 'Profile/operations.json' && entry.status === 'missing',
    ),
  );
  const files = entries(archive.bytes);
  assert.match(files['README.txt']!, /INCOMPLETE COLLECTION/);
  assert.equal(files['Profile/activity.jsonl'], undefined);
  assert.equal(fs.readFileSync(file, 'utf8'), '{"truncated":\n');
});

test('invalid startup filenames and corrupt update-operation names remain visible as recovery evidence', (t) => {
  const f = fixture(t);
  f.write(
    'Profile/startup-continuations/invalid.json',
    '{"Detail":"Invalid filename blocks startup"}',
  );
  f.write(
    'Profile/startup-continuations/UPPERCASE.JSON',
    '{"Detail":"Case-insensitive Windows startup evidence"}',
  );
  let archive = createDiagnosticArchive(f.input);
  let files = entries(archive.bytes);
  assert.equal(archive.complete, true);
  assert.match(files['Profile/startup-continuations/invalid.json']!, /blocks startup/);
  assert.match(files['Profile/startup-continuations/UPPERCASE.JSON']!, /Windows startup evidence/);
  f.write('Profile/startup-continuations/corrupt.JSON', '{"unfinished":');
  const corruptOperation = 'a'.repeat(36);
  f.write(
    `Updates/operations/${corruptOperation}/ready.json`,
    '{"Detail":"Corrupt operation name blocks recovery"}',
  );
  archive = createDiagnosticArchive(f.input);
  files = entries(archive.bytes);
  assert.equal(archive.complete, false);
  assert.ok(
    archive.manifest.issues.some(
      (issue) =>
        issue.file === 'Profile/startup-continuations/corrupt.JSON' &&
        issue.reason === 'invalid-format',
    ),
  );
  assert.ok(
    archive.manifest.issues.some(
      (issue) =>
        issue.file === 'Updates/operations/' + corruptOperation &&
        issue.reason === 'invalid-format',
    ),
  );
  assert.match(files[`Updates/operations/${corruptOperation}/ready.json`]!, /blocks recovery/);
});

test('API-key credentials embedded in Activity details, runtime errors and helper CLI text are redacted', (t) => {
  const f = fixture(t);
  const secret = 'test-private-api-credential';
  f.write(
    'Profile/activity.jsonl',
    JSON.stringify({ Detail: `Provider failed: api_key=${secret}; code=AccessDenied` }) + '\n',
  );
  f.write(
    `Updates/operations/${operationId}/helper.log`,
    `--api-key=${secret}\n--API_KEY "${secret}"\n--apikey ${secret}\nAPI-KEY: ${secret}\nhttps://example.com/file?api_key=${secret}&version=1.6.0\n`,
  );
  f.input.runtime = {
    error: `Cannot open C:\\Users\\Bezor\\Data: apiKey=${secret}; version=1.6.0`,
  };
  const archive = createDiagnosticArchive(f.input);
  const text = Object.values(entries(archive.bytes)).join('\n');
  assert.equal(archive.complete, true);
  assert.doesNotMatch(text, /test-private-api-credential/);
  assert.match(text, /AccessDenied/);
  assert.match(text, /version=1\.6\.0/);
  assert.match(text, /Users\\\\Bezor/);
});

test('current runtime errors and startup details are included with recursive credential redaction', (t) => {
  const f = fixture(t);
  f.input.runtime = {
    backend: {
      connection: 'failed',
      error: 'Cannot load C:\\Users\\Bezor\\Portable\\Data\\Profile: Access denied',
      token: 'private-backend-token',
    },
    updates: {
      phase: 'error',
      error:
        'Move from D:\\Portable\\App to D:\\Portable\\backup failed: sharing violation; Authorization: Bearer private-github-token',
    },
    startup: {
      launchManagerAtLogin: true,
      launchManagerAsAdministrator: true,
      issues: [{ code: 'StartupEntryConflict', detail: 'BEZORO\\Bezor and SID did not match' }],
    },
    scripts: [
      { id: operationId, path: 'C:\\Scripts\\outside.ahk', runAtLogin: true, status: 'Running' },
    ],
  };
  const archive = createDiagnosticArchive(f.input);
  assert.equal(archive.complete, true);
  const runtime = entries(archive.bytes)['runtime.json']!;
  assert.match(runtime, /Access denied|sharing violation/);
  assert.match(runtime, /StartupEntryConflict/);
  assert.match(runtime, /BEZORO\\\\Bezor/);
  assert.match(runtime, /outside\.ahk/);
  assert.doesNotMatch(runtime, /private-backend-token|private-github-token/);
  assert.equal(JSON.parse(runtime).startup.launchManagerAsAdministrator, true);
});

test('runtime serialization and redaction growth never silently overrun declared archive limits', (t) => {
  const f = fixture(t);
  const cyclic: Record<string, unknown> = {};
  cyclic.self = cyclic;
  f.input.runtime = cyclic;
  let archive = createDiagnosticArchive(f.input);
  assert.equal(archive.complete, false);
  assert.ok(
    archive.manifest.issues.some(
      (issue) => issue.file === 'runtime.json' && issue.reason === 'invalid-format',
    ),
  );
  f.input.runtime = Array.from({ length: 50 }, () => ({ token: '', detail: 'entry' }));
  archive = createDiagnosticArchive(f.input, { maximumFileBytes: 2048 });
  assert.equal(archive.complete, false);
  assert.ok(
    archive.manifest.issues.some(
      (issue) => issue.file === 'runtime.json' && issue.reason === 'size-limit',
    ),
  );
});

test('linked or concurrently changed evidence is skipped explicitly without changing source files', (t) => {
  const f = fixture(t);
  const original = f.write('real.jsonl', '{"detail":"original"}\n');
  fs.linkSync(original, path.join(f.input.deployment.profileDirectory, 'activity.jsonl'));
  let archive = createDiagnosticArchive(f.input);
  assert.equal(archive.complete, false);
  assert.ok(archive.manifest.issues.some((issue) => issue.reason === 'unsafe-or-changed'));
  fs.unlinkSync(path.join(f.input.deployment.profileDirectory, 'activity.jsonl'));
  const activity = f.write('Profile/activity.jsonl', '{"detail":"initial"}\n');
  const originalRead = fs.readSync;
  let changed = false;
  t.mock.method(fs, 'readSync', (...args: Parameters<typeof fs.readSync>) => {
    const count = Reflect.apply(originalRead, fs, args) as number;
    if (!changed) {
      changed = true;
      fs.appendFileSync(activity, '{"detail":"concurrent"}\n');
    }
    return count;
  });
  archive = createDiagnosticArchive(f.input);
  assert.equal(archive.complete, false);
  assert.ok(
    archive.manifest.issues.some(
      (issue) => issue.file === 'Profile/activity.jsonl' && issue.reason === 'unsafe-or-changed',
    ),
  );
  assert.match(fs.readFileSync(activity, 'utf8'), /concurrent/);
});

test('a truncated final Activity record keeps all preceding evidence and explicitly marks recovery as incomplete', (t) => {
  const f = fixture(t);
  const valid = Array.from({ length: 450 }, (_, index) =>
    JSON.stringify({ id: index, detail: 'Access denied before interruption' }),
  ).join('\n');
  const source = valid + '\n{"detail":"unfinished';
  const original = f.write('Profile/activity.jsonl', source);
  const archive = createDiagnosticArchive(f.input);
  assert.equal(archive.complete, false);
  const files = entries(archive.bytes);
  assert.equal(files['Profile/activity.jsonl'], valid);
  assert.ok(
    archive.manifest.issues.some(
      (issue) =>
        issue.file === 'Profile/activity.jsonl' &&
        issue.reason === 'invalid-format' &&
        issue.count === 1,
    ),
  );
  assert.equal(fs.readFileSync(original, 'utf8'), source);
});

test('redirected diagnostic roots do not collect target data', (t) => {
  const f = fixture(t);
  const target = path.join(f.root, 'foreign');
  fs.mkdirSync(target);
  fs.writeFileSync(path.join(target, 'activity.jsonl'), '{"detail":"foreign marker"}\n');
  fs.rmdirSync(f.input.deployment.profileDirectory);
  fs.symlinkSync(
    target,
    f.input.deployment.profileDirectory,
    process.platform === 'win32' ? 'junction' : 'dir',
  );
  const archive = createDiagnosticArchive(f.input);
  assert.equal(archive.complete, false);
  assert.doesNotMatch(Object.values(entries(archive.bytes)).join('\n'), /foreign marker/);
});

test('a crash inside a UTF-8 character preserves every complete prior Activity record without fabricating replacement text', (t) => {
  const f = fixture(t);
  const valid =
    Array.from({ length: 450 }, (_, index) =>
      JSON.stringify({ id: index, detail: 'C:\\Users\\João\\Scripts\\élevé.ahk access denied' }),
    ).join('\n') + '\n';
  const source = Buffer.concat([Buffer.from(valid), Buffer.from('{"detail":"á').subarray(0, -1)]);
  const original = f.write('Profile/activity.jsonl', '');
  fs.writeFileSync(original, source);
  const archive = createDiagnosticArchive(f.input);
  assert.equal(archive.complete, false);
  const copied = entries(archive.bytes)['Profile/activity.jsonl']!;
  assert.equal(copied, valid);
  assert.equal(copied.trim().split('\n').length, 450);
  assert.doesNotMatch(copied, /\uFFFD/);
  assert.ok(
    archive.manifest.issues.some(
      (issue) =>
        issue.file === 'Profile/activity.jsonl' &&
        issue.reason === 'invalid-format' &&
        issue.count === 1,
    ),
  );
  assert.equal(
    archive.manifest.entries.find((entry) => entry.file === 'Profile/activity.jsonl')!.sourceBytes,
    source.length,
  );
  assert.deepEqual(fs.readFileSync(original), source);
});

test('size, read and entry limits produce an explicitly incomplete manifest instead of a silent tail', (t) => {
  const f = fixture(t);
  f.write('Profile/activity.jsonl', JSON.stringify({ detail: 'x'.repeat(1200) }) + '\n');
  let archive = createDiagnosticArchive(f.input, { maximumFileBytes: 1024 });
  assert.ok(archive.manifest.issues.some((issue) => issue.reason === 'size-limit'));
  archive = createDiagnosticArchive(f.input, { maximumTotalBytes: 1024 });
  assert.ok(archive.manifest.issues.some((issue) => issue.reason === 'read-budget'));
  archive = createDiagnosticArchive(f.input, { maximumFiles: 1 });
  assert.ok(archive.manifest.issues.some((issue) => issue.reason === 'entry-limit'));
  for (const result of [archive]) assert.equal(result.complete, false);
});

test('text redaction preserves diagnostic prose while removing authenticated URL and header credentials', () => {
  const result = redactDiagnosticText(
    'Helper admission failed\nhttps://me:password@example.com/package?token=value&version=1.6.0\nBearer abcdefghijklmnop\npassword="hidden value"\n',
  );
  assert.match(result.contents, /Helper admission failed/);
  assert.match(result.contents, /version=1\.6\.0/);
  assert.doesNotMatch(result.contents, /me:password|token=value|abcdefghijklmnop|hidden value/);
  const nested = redactDiagnosticText(
    'The provider returned {"authToken":"do-not-copy-this","code":"AccessDenied"}',
    false,
  );
  assert.doesNotMatch(nested.contents, /do-not-copy-this/);
  assert.match(nested.contents, /AccessDenied/);
  assert.throws(
    () => redactDiagnosticText('{"token":"hidden","token":"[REDACTED]"}', true),
    /duplicate/,
  );
});
