import test, { type TestContext } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { randomUUID } from 'node:crypto';
import { CommandLog, commandDetails } from '../../src/main/logging/command-log.js';

function fixture(t: TestContext) {
  const parent = process.env.AHK_DIAGNOSTICS_TEST_ROOT;
  assert.ok(parent && path.isAbsolute(parent));
  const directory = fs.mkdtempSync(path.join(parent, 'command-log-'));
  t.after(() => fs.rmSync(directory, { recursive: true, force: true }));
  return directory;
}

function records(directory: string): Record<string, unknown>[] {
  return fs
    .readdirSync(path.join(directory, 'Logs'))
    .sort()
    .flatMap((name) =>
      fs
        .readFileSync(path.join(directory, 'Logs', name), 'utf8')
        .trim()
        .split('\n')
        .map((line) => JSON.parse(line) as Record<string, unknown>),
    );
}

test('shortcut retry diagnostics retain bounded attempts and OS codes without raw error text', async (t) => {
  const directory = fixture(t);
  const log = new CommandLog(directory);
  t.after(() => log.close());
  const error = Object.assign(new Error('private source path and OS message'), { code: 'EPERM' });
  await assert.rejects(
    log.run(
      'lifecycle',
      'repairSearchShortcut.rename',
      () => {
        throw error;
      },
      { attempt: 1, retrying: true },
    ),
    (value) => value === error,
  );
  const complete = records(directory).find((record) => record.phase === 'error')!;
  assert.equal(complete.command, 'repairSearchShortcut.rename');
  assert.equal(complete.attempt, 1);
  assert.equal(complete.retrying, true);
  assert.equal(complete.errorCode, 'EPERM');
  assert.doesNotMatch(JSON.stringify(records(directory)), /private source path/);
  const copied = log.snapshot('errors').records.at(-1)!;
  assert.equal(copied.command, 'repairSearchShortcut.rename');
  assert.equal(copied.attempt, 1);
  assert.equal(copied.retrying, true);
  assert.equal(copied.errorCode, 'EPERM');
});

test('successful, declined, cancelled and failed commands retain scoped intent and outcomes without raw payloads', async (t) => {
  const directory = fixture(t);
  const log = new CommandLog(directory);
  t.after(() => log.close());
  const scriptId = randomUUID();
  const correlationId = randomUUID();
  const secret = 'github_pat_secret-sentinel-do-not-store';
  const payload = {
    selection: { scriptId },
    operation: 'Start',
    token: secret,
    scriptContents: secret,
    clipboard: secret,
    arguments: [secret],
    metadata: { runAtLogin: true, runAsAdministrator: false, friendlyName: secret },
  };
  const returned = { outcome: 'Confirmed', code: 'None', detail: secret, correlationId };
  assert.equal(await log.run('ui', 'operate', () => returned, commandDetails(payload)), returned);
  await log.run('ui', 'pickEditor', () => null);
  await log.run('lifecycle', 'startManager', () => false);
  await log.run('backend', 'addFiles', () => ({
    result: { outcome: 'Partial', code: 'InvalidPath', detail: secret },
    added: ['secret-path'],
    duplicateCount: 2,
    issues: [{ detail: secret }],
  }));
  await log.run('updates', 'check', () => ({ phase: 'error', error: secret, releases: [secret] }));
  await log.run('backend', 'prepare', () => ({
    plan: { allowed: false, reason: 'StaleSelection', expectedIdentity: secret },
  }));
  await log.run('backend', 'administrationStatus', () => ({
    state: 'Denied',
    reason: 'AccessDenied',
    receiptId: secret,
  }));
  await log.run('storage', 'choose', () => ({
    state: { error: secret, currentDataDirectory: secret },
    restartRequired: false,
  }));
  const events = records(directory).filter((record) => record.kind === 'command');
  assert.equal(events.length, 16);
  assert.deepEqual(events[0], {
    schemaVersion: 1,
    sessionId: events[0]!.sessionId,
    timestamp: events[0]!.timestamp,
    kind: 'command',
    source: 'ui',
    command: 'operate',
    operation: 'Start',
    scriptId,
    runAtLogin: true,
    runAsAdministrator: false,
    requestId: events[0]!.requestId,
    parentRequestId: null,
    phase: 'started',
  });
  assert.equal(events[1]!.requestId, events[0]!.requestId);
  assert.equal(events[1]!.correlationId, correlationId);
  assert.equal(events[1]!.outcome, 'Confirmed');
  assert.equal(events[1]!.code, 'None');
  assert.equal(typeof events[1]!.durationMs, 'number');
  assert.equal(events[3]!.result, 'cancelled');
  assert.equal(events[5]!.result, 'declined');
  assert.equal(events[7]!.outcome, 'Partial');
  assert.equal(events[7]!.addedCount, 1);
  assert.equal(events[7]!.duplicateCount, 2);
  assert.equal(events[7]!.issueCount, 1);
  assert.equal(events[9]!.result, 'failed');
  assert.equal(events[11]!.result, 'refused');
  assert.equal(events[11]!.code, 'StaleSelection');
  assert.equal(events[13]!.state, 'Denied');
  assert.equal(events[13]!.code, 'AccessDenied');
  assert.equal(events[15]!.result, 'failed');
  assert.ok(!JSON.stringify(events).includes(secret));
  assert.ok(!JSON.stringify(events).includes('secret-path'));
  assert.equal(log.status().available, true);
});

test('resolved failure, cancellation and uncertain dispatch results never appear as successful command completion', async (t) => {
  const directory = fixture(t);
  const log = new CommandLog(directory);
  t.after(() => log.close());
  for (const outcome of [
    'Failed',
    'Partial',
    'Unknown',
    'Pending',
    'Accepted',
    'CancelledBeforeDispatch',
    'Confirmed',
  ])
    await log.run('backend', 'perform', () => ({ outcome, code: 'None' }));
  await log.run('ui', 'pickEditor', () => []);
  const events = records(directory).filter((record) => record.phase === 'completed');
  assert.deepEqual(
    events.slice(0, 7).map((record) => record.result),
    ['failed', 'partial', 'unknown', 'pending', 'accepted', 'cancelled', 'confirmed'],
  );
  assert.equal(events[7]!.itemCount, 0);
});

test('shortcut registration preserves its finite results and never records arbitrary returned strings', async (t) => {
  const directory = fixture(t);
  const log = new CommandLog(directory);
  t.after(() => log.close());
  const results = ['created', 'updated', 'unchanged', 'preserved', 'not-applicable', 'failed'];
  for (const result of results) await log.run('lifecycle', 'ensureSearchShortcut', () => result);
  const secret = 'github_pat_arbitrary-returned-string-do-not-store';
  assert.equal(await log.run('lifecycle', 'ensureSearchShortcut', () => secret), secret);
  const events = records(directory).filter((record) => record.phase === 'completed');
  assert.deepEqual(
    events.map((record) => record.result),
    [...results, 'completed'],
  );
  assert.ok(!JSON.stringify(records(directory)).includes(secret));
});

test('nested and concurrent commands correlate their own backend request without crossing asynchronous scopes', async (t) => {
  const directory = fixture(t);
  const log = new CommandLog(directory);
  t.after(() => log.close());
  let release!: () => void;
  const gate = new Promise<void>((resolve) => (release = resolve));
  const first = log.run('ui', 'operate', async () => {
    await gate;
    return log.run('backend', 'perform', () => ({ outcome: 'Confirmed', code: 'None' }));
  });
  await log.run('ui', 'saveSettings', () =>
    log.run('backend', 'saveSettings', () => ({ outcome: 'Confirmed', code: 'None' })),
  );
  release();
  await first;
  const events = records(directory).filter((record) => record.phase === 'started');
  const operate = events.find((record) => record.source === 'ui' && record.command === 'operate')!;
  const settings = events.find(
    (record) => record.source === 'ui' && record.command === 'saveSettings',
  )!;
  const perform = events.find(
    (record) => record.source === 'backend' && record.command === 'perform',
  )!;
  const backendSettings = events.find(
    (record) => record.source === 'backend' && record.command === 'saveSettings',
  )!;
  assert.equal(operate.parentRequestId, null);
  assert.equal(settings.parentRequestId, null);
  assert.equal(perform.parentRequestId, operate.requestId);
  assert.equal(backendSettings.parentRequestId, settings.requestId);
  assert.notEqual(perform.parentRequestId, backendSettings.parentRequestId);
  assert.equal(new Set(events.map((record) => record.requestId)).size, 4);
});

test('throwing commands run once and preserve the exact exception while logging only finite error classifications', async (t) => {
  const directory = fixture(t);
  const log = new CommandLog(directory);
  t.after(() => log.close());
  const secret = 'https://example.invalid/download?token=github_pat_error-secret';
  const error = Object.assign(new Error(secret), {
    code: 'EACCES',
    requestId: '42',
    stack: secret,
  });
  let calls = 0;
  await assert.rejects(
    log.run('backend', 'perform', () => {
      calls++;
      throw error;
    }),
    (caught) => caught === error,
  );
  assert.equal(calls, 1);
  const event = records(directory).find((record) => record.phase === 'error')!;
  assert.equal(event.errorName, 'Error');
  assert.equal(event.errorCode, 'EACCES');
  assert.equal(event.backendRequestId, '42');
  assert.equal(event.result, 'threw');
  assert.ok(!JSON.stringify(records(directory)).includes(secret));
  assert.equal('message' in event, false);
  assert.equal('stack' in event, false);
});

test('safe detail selection excludes invalid IDs, paths, tokens, channel aliases and arbitrary preference values', () => {
  const scriptId = randomUUID();
  assert.deepEqual(
    commandDetails({
      currentSelection: { scriptId },
      operation: 'Restart',
      channel: 'test',
      version: '1.4.1-test.53.1.9',
      settings: {
        launchManagerAtLogin: true,
        launchManagerAsAdministrator: false,
        startInTray: true,
        editorArguments: ['secret'],
      },
      session: 'secret',
    }),
    {
      scriptId,
      operation: 'Restart',
      channel: 'test',
      version: '1.4.1-test.53.1.9',
      launchManagerAtLogin: true,
      launchManagerAsAdministrator: false,
      startInTray: true,
    },
  );
  assert.deepEqual(
    commandDetails({
      scriptId: 'secret',
      operation: 'delete-everything',
      channel: 'secret',
      version: 'github_pat_secret',
      metadata: { runAtLogin: 'true', runAsAdministrator: 'secret' },
    }),
    {},
  );
  assert.deepEqual(commandDetails(null), {});
  assert.deepEqual(commandDetails([]), {});
  assert.deepEqual(commandDetails({ metadata: { runAtLogin: null } }), { runAtLogin: null });
});

test('rotation bounds verified log history across sessions and preserves unrelated or unrecognized files', async (t) => {
  const directory = fixture(t);
  fs.mkdirSync(path.join(directory, 'Logs'));
  const foreign = path.join(
    directory,
    'Logs',
    `commands-20000101T000000000Z-${randomUUID()}-000000.jsonl`,
  );
  const contents = 'Unrelated user document, despite its log-like name.\n';
  fs.writeFileSync(foreign, contents);
  const unrecognizedSession = randomUUID();
  const unrecognizedFile = path.join(
    directory,
    'Logs',
    `commands-20000101T000000000Z-${unrecognizedSession}-000000.jsonl`,
  );
  const unrecognizedContents =
    JSON.stringify({
      schemaVersion: 1,
      kind: 'session',
      applicationId: 'Bezoro.AhkManager.Electron',
      sessionId: unrecognizedSession,
      timestamp: '2000-01-01T00:00:00.000Z',
      pid: process.pid,
      fileIndex: 0,
      userDocument: 'Preserve this additional field.',
    }) + '\n';
  fs.writeFileSync(unrecognizedFile, unrecognizedContents);
  fs.writeFileSync(path.join(directory, 'Logs', 'readme.txt'), 'Unrelated file.');
  for (let session = 0; session < 2; session++) {
    const log = new CommandLog(directory, { maximumFileBytes: 4096, maximumFiles: 3 });
    for (let index = 0; index < 70; index++)
      await log.run('ui', `refresh${index}`, () => undefined);
    assert.equal(log.status().available, true);
    log.close();
  }
  const files = fs
    .readdirSync(path.join(directory, 'Logs'))
    .filter(
      (name) =>
        name.endsWith('.jsonl') &&
        path.join(directory, 'Logs', name) !== foreign &&
        path.join(directory, 'Logs', name) !== unrecognizedFile,
    );
  assert.equal(files.length, 3);
  for (const name of files) {
    const file = path.join(directory, 'Logs', name);
    assert.ok(fs.statSync(file).size <= 4096);
    const events = fs
      .readFileSync(file, 'utf8')
      .trim()
      .split('\n')
      .map((line) => JSON.parse(line) as Record<string, unknown>);
    assert.equal(events[0]!.kind, 'session');
  }
  assert.ok(
    files.some((name) =>
      fs.readFileSync(path.join(directory, 'Logs', name), 'utf8').includes('refresh69'),
    ),
  );
  assert.equal(fs.readFileSync(foreign, 'utf8'), contents);
  assert.equal(fs.readFileSync(unrecognizedFile, 'utf8'), unrecognizedContents);
  assert.equal(
    fs.readFileSync(path.join(directory, 'Logs', 'readme.txt'), 'utf8'),
    'Unrelated file.',
  );
});

test('logging failure does not change command results or exceptions, replay commands or write through a redirected directory', async (t) => {
  const directory = fixture(t);
  const external = path.join(directory, 'External');
  fs.mkdirSync(external);
  fs.symlinkSync(
    external,
    path.join(directory, 'Logs'),
    process.platform === 'win32' ? 'junction' : 'dir',
  );
  const log = new CommandLog(directory);
  assert.equal(log.status().available, false);
  assert.deepEqual(fs.readdirSync(external), []);
  const returned = { outcome: 'Confirmed', code: 'None' };
  let calls = 0;
  assert.equal(
    await log.run('ui', 'refresh', () => {
      calls++;
      return returned;
    }),
    returned,
  );
  const error = new Error('Original command failure.');
  await assert.rejects(
    log.run('backend', 'perform', () => {
      calls++;
      throw error;
    }),
    (caught) => caught === error,
  );
  assert.equal(calls, 2);
  log.close();
  assert.deepEqual(fs.readdirSync(external), []);
});

test('active file hardlinks and path replacements disable logging without modifying unknown files', async (t) => {
  for (const mutate of ['hardlink', 'replacement'] as const) {
    const directory = fixture(t);
    const log = new CommandLog(directory);
    const file = path.join(log.directory, fs.readdirSync(log.directory)[0]!);
    const before = fs.readFileSync(file);
    const external = path.join(directory, 'external-file');
    if (mutate === 'hardlink') fs.linkSync(file, external);
    else {
      // The open log remains under another name; a replacement must never receive appended bytes.
      fs.renameSync(file, external);
      fs.writeFileSync(file, 'Foreign replacement.');
    }
    let calls = 0;
    assert.equal(await log.run('ui', 'refresh', () => ++calls), 1);
    assert.equal(calls, 1);
    assert.equal(log.status().available, false);
    assert.equal(log.status().failure, 'unsafe-path');
    assert.deepEqual(fs.readFileSync(external), before);
    if (mutate === 'replacement')
      assert.equal(fs.readFileSync(file, 'utf8'), 'Foreign replacement.');
    log.close();
  }
});

test('closed logs remain closed while commands keep their original behavior', async (t) => {
  const directory = fixture(t);
  const log = new CommandLog(directory);
  await log.run('ui', 'refresh', () => undefined);
  log.close();
  const before = records(directory);
  assert.equal(await log.run('ui', 'refresh', () => 7), 7);
  log.close();
  assert.deepEqual(records(directory), before);
  assert.equal(log.status().failure, 'closed');
  assert.equal(before.at(-1)!.kind, 'session-end');
});

test('metadata-only changes do not disable an active log, while same-size modified contents still do', async (t) => {
  const directory = fixture(t);
  const log = new CommandLog(directory);
  t.after(() => log.close());
  const file = path.join(log.directory, fs.readdirSync(log.directory)[0]!);
  const initial = fs.lstatSync(file, { bigint: true });
  await new Promise((resolve) => setTimeout(resolve, 2));
  fs.chmodSync(file, 0o400);
  fs.chmodSync(file, 0o600);
  const metadata = fs.lstatSync(file, { bigint: true });
  assert.equal(metadata.ino, initial.ino);
  assert.equal(metadata.size, initial.size);
  assert.equal(metadata.mtimeNs, initial.mtimeNs);
  if (process.platform !== 'win32') assert.notEqual(metadata.ctimeNs, initial.ctimeNs);
  assert.equal(await log.run('ui', 'refresh', () => 1), 1);
  assert.equal(log.status().available, true);

  const before = fs.lstatSync(file, { bigint: true });
  const edited = Buffer.from(fs.readFileSync(file));
  edited[edited.length - 2] = edited[edited.length - 2] === 32 ? 33 : 32;
  fs.writeFileSync(file, edited);
  const changed = new Date(Number(before.mtimeNs / 1000000n) + 5000);
  fs.utimesSync(file, changed, changed);
  assert.equal(fs.lstatSync(file, { bigint: true }).size, before.size);
  assert.equal(await log.run('ui', 'refresh', () => 2), 2);
  assert.equal(log.status().available, false);
  assert.equal(log.status().failure, 'unsafe-path');
  assert.deepEqual(fs.readFileSync(file), edited);
});

test('diagnostic snapshots include retained sessions and current commands without changing logging', async (t) => {
  const directory = fixture(t);
  const previous = new CommandLog(directory);
  await previous.run('ui', 'refresh', () => undefined);
  previous.close();
  await new Promise((resolve) => setTimeout(resolve, 2));
  const log = new CommandLog(directory);
  t.after(() => log.close());
  await log.run('ui', 'saveSettings', () => ({ outcome: 'Confirmed', code: 'None' }));
  const before = fs.readdirSync(log.directory).map((name) => ({
    name,
    bytes: fs.readFileSync(path.join(log.directory, name)),
  }));
  const snapshot = log.snapshot('recent');
  assert.deepEqual(
    snapshot.records.map((record) => record.command),
    ['refresh', 'refresh', 'saveSettings', 'saveSettings'],
  );
  assert.equal(new Set(snapshot.records.map((record) => record.sessionId)).size, 2);
  assert.equal(snapshot.omittedRecords, 0);
  assert.equal(snapshot.skippedFiles, 0);
  for (const file of before)
    assert.deepEqual(fs.readFileSync(path.join(log.directory, file.name)), file.bytes);
  assert.equal(log.status().available, true);
  await log.run('ui', 'refresh', () => 7);
  assert.equal(log.snapshot('recent').records.length, 6);
});

test('error snapshots retain failed requests and parent context while excluding routine cancellations', async (t) => {
  const directory = fixture(t);
  const log = new CommandLog(directory);
  t.after(() => log.close());
  await log.run('ui', 'operate', () =>
    log.run('backend', 'perform', () => ({ outcome: 'Partial', code: 'AccessDenied' })),
  );
  await log.run('ui', 'pickEditor', () => null);
  await log.run('ui', 'confirm', () => false);
  await log.run('backend', 'prepare', () => ({ allowed: false, reason: 'StaleSelection' }));
  await log.run('backend', 'administrationStatus', () => ({
    state: 'Unknown',
    reason: 'PendingOperation',
  }));
  await assert.rejects(
    log.run('updates', 'check', () => {
      throw Object.assign(new Error('private message'), { code: 'ETIMEDOUT' });
    }),
  );
  const snapshot = log.snapshot('errors');
  assert.deepEqual(
    snapshot.records.map((record) => record.command),
    [
      'operate',
      'perform',
      'perform',
      'operate',
      'prepare',
      'prepare',
      'administrationStatus',
      'administrationStatus',
      'check',
      'check',
    ],
  );
  assert.ok(snapshot.records.some((record) => record.parentRequestId !== null));
  assert.ok(snapshot.records.some((record) => record.result === 'refused'));
  assert.ok(snapshot.records.some((record) => record.errorCode === 'ETIMEDOUT'));
  assert.ok(snapshot.records.some((record) => record.state === 'Unknown'));
  assert.doesNotMatch(JSON.stringify(snapshot), /private message|cancelled|declined/);
});

test('diagnostic snapshots cap recent records and report omitted history across rotations', async (t) => {
  const directory = fixture(t);
  const log = new CommandLog(directory);
  t.after(() => log.close());
  for (let index = 0; index < 130; index++) await log.run('ui', 'refresh', () => undefined);
  const snapshot = log.snapshot('recent');
  assert.equal(snapshot.records.length, 200);
  assert.equal(snapshot.omittedRecords, 60);
  assert.equal(log.snapshot('errors').records.length, 0);
  const rotatedDirectory = fixture(t);
  const rotated = new CommandLog(rotatedDirectory, { maximumFileBytes: 4096, maximumFiles: 3 });
  t.after(() => rotated.close());
  for (let index = 0; index < 50; index++) await rotated.run('ui', 'refresh', () => undefined);
  const retained = rotated.snapshot('recent');
  assert.ok(retained.records.length > 0 && retained.records.length < 100);
  assert.equal(retained.skippedFiles, 0);
  assert.ok(retained.records.every((record) => record.command === 'refresh'));
});

test('copying diagnostics reconstructs finite values and excludes forged secrets in recognized fields', async (t) => {
  const directory = fixture(t);
  const log = new CommandLog(directory);
  await log.run('backend', 'perform', () => ({ outcome: 'Failed', code: 'AccessDenied' }));
  log.close();
  const file = path.join(log.directory, fs.readdirSync(log.directory)[0]!);
  const lines = fs
    .readFileSync(file, 'utf8')
    .trim()
    .split('\n')
    .map((line) => JSON.parse(line));
  const secret = 'github_pat_private-sentinel';
  const template = lines.find((record) => record.phase === 'completed');
  const forged = {
    ...template,
    command: secret,
    outcome: secret,
    code: secret,
    errorCode: secret,
    errorName: secret,
    state: secret,
    result: secret,
    version: `1.5.0-${secret}`,
    detail: secret,
    token: secret,
    contents: secret,
    addedCount: -1,
    durationMs: secret,
    backendRequestId: secret,
    attempt: 100000,
    retrying: secret,
    runAtLogin: secret,
    scriptId: secret,
    correlationId: secret,
  };
  const invalid = [
    { ...forged, timestamp: secret },
    { ...forged, source: secret },
    { ...forged, requestId: secret },
    { ...forged, parentRequestId: secret },
    { ...forged, timestamp: '2026-02-30T00:00:00.000Z' },
  ];
  fs.writeFileSync(
    file,
    [lines[0], forged, ...invalid].map((record) => JSON.stringify(record)).join('\n') + '\n',
  );
  const snapshot = log.snapshot('recent');
  assert.equal(snapshot.records.length, 1);
  assert.equal(snapshot.records[0]!.command, 'unknown');
  assert.doesNotMatch(JSON.stringify(snapshot), /private-sentinel|token|detail|contents/);
  assert.equal('version' in snapshot.records[0]!, false);
  assert.equal('durationMs' in snapshot.records[0]!, false);
  assert.equal('attempt' in snapshot.records[0]!, false);
  assert.equal('outcome' in snapshot.records[0]!, false);
  assert.equal(log.status().failure, 'closed');
});

test('snapshots skip malformed, oversized and linked log-like files without touching them', async (t) => {
  const directory = fixture(t);
  const log = new CommandLog(directory);
  await log.run('ui', 'refresh', () => undefined);
  const validFile = path.join(log.directory, fs.readdirSync(log.directory)[0]!);
  const external = path.join(directory, 'external-private.txt');
  const sentinel = 'private external document';
  fs.writeFileSync(external, sentinel);
  const malformed = path.join(
    log.directory,
    `commands-20000101T000000000Z-${randomUUID()}-000000.jsonl`,
  );
  const oversized = path.join(
    log.directory,
    `commands-20000101T000000000Z-${randomUUID()}-000001.jsonl`,
  );
  const hardlink = path.join(
    log.directory,
    `commands-20000101T000000000Z-${randomUUID()}-000002.jsonl`,
  );
  fs.writeFileSync(malformed, sentinel);
  fs.writeFileSync(oversized, Buffer.alloc(2 * 1024 * 1024 + 1, 65));
  fs.linkSync(external, hardlink);
  const before = fs.readFileSync(validFile);
  const snapshot = log.snapshot('recent');
  assert.equal(snapshot.records.length, 2);
  assert.equal(snapshot.skippedFiles, 3);
  assert.doesNotMatch(JSON.stringify(snapshot), /private external/);
  assert.equal(fs.readFileSync(external, 'utf8'), sentinel);
  assert.equal(fs.readFileSync(malformed, 'utf8'), sentinel);
  assert.equal(fs.lstatSync(oversized).size, 2 * 1024 * 1024 + 1);
  assert.deepEqual(fs.readFileSync(validFile), before);
  assert.equal(log.status().available, true);
  log.close();
});

test('a redirected or replaced log directory cannot be copied and read failure does not disable logging', async (t) => {
  const directory = fixture(t);
  const log = new CommandLog(directory);
  await log.run('ui', 'refresh', () => undefined);
  const original = path.join(directory, 'OriginalLogs');
  const external = path.join(directory, 'External');
  fs.mkdirSync(external);
  fs.renameSync(log.directory, original);
  fs.symlinkSync(external, log.directory, process.platform === 'win32' ? 'junction' : 'dir');
  assert.throws(() => log.snapshot('recent'), /Diagnostic logs could not be read/);
  assert.equal(log.status().available, true);
  assert.deepEqual(fs.readdirSync(external), []);
  fs.unlinkSync(log.directory);
  fs.renameSync(original, log.directory);
  assert.equal(await log.run('ui', 'refresh', () => 1), 1);
  assert.equal(log.status().available, true);
  assert.equal(log.snapshot('recent').records.length, 4);
  log.close();
});

test('snapshot copies reject changed files and still read retained evidence after logging has failed', async (t) => {
  const directory = fixture(t);
  const previous = new CommandLog(directory);
  await previous.run('ui', 'refresh', () => undefined);
  previous.close();
  await new Promise((resolve) => setTimeout(resolve, 2));
  const log = new CommandLog(directory);
  const file = path.join(log.directory, fs.readdirSync(log.directory).sort().at(-1)!);
  fs.writeFileSync(file, 'unknown private contents');
  await log.run('ui', 'saveSettings', () => 1);
  assert.equal(log.status().available, false);
  const snapshot = log.snapshot('recent');
  assert.deepEqual(
    snapshot.records.map((record) => record.command),
    ['refresh', 'refresh'],
  );
  assert.equal(snapshot.skippedFiles, 1);
  assert.doesNotMatch(JSON.stringify(snapshot), /private contents/);
  log.close();
});

test('a log changed during a snapshot is omitted without disclosing its partial contents', async (t) => {
  const directory = fixture(t);
  const log = new CommandLog(directory);
  await log.run('ui', 'refresh', () => undefined);
  log.close();
  const file = path.join(log.directory, fs.readdirSync(log.directory)[0]!);
  const originalRead = fs.readSync;
  let changed = false;
  const mock = t.mock.method(fs, 'readSync', (...args: Parameters<typeof fs.readSync>) => {
    const count = originalRead(...args);
    if (!changed) {
      changed = true;
      fs.appendFileSync(file, 'private concurrently appended text\n');
    }
    return count;
  });
  const snapshot = log.snapshot('recent');
  mock.mock.restore();
  assert.equal(changed, true);
  assert.equal(snapshot.records.length, 0);
  assert.equal(snapshot.skippedFiles, 1);
  assert.doesNotMatch(JSON.stringify(snapshot), /private concurrently/);
});
