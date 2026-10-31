import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import {
  createDiagnosticReport,
  type DiagnosticReportContext,
} from '../../src/main/logging/diagnostic-report.js';
import { CommandLog } from '../../src/main/logging/command-log.js';
import { emptyUpdateState } from '../../src/shared/updates.js';
import { snapshot } from './fixtures.js';

function context(): DiagnosticReportContext {
  return {
    version: '1.6.0',
    platform: 'win32',
    architecture: 'x64',
    operatingSystemRelease: '10.0.26100',
    deployment: 'portable',
    host: {
      snapshot: snapshot(),
      connection: 'ready',
      error: null,
      busy: false,
      authorityMode: 'Administrator',
      administrationPending: false,
    },
    updates: emptyUpdateState(),
  };
}

test('diagnostic reports include actionable state and safe Activity codes without paths or messages', async (t) => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'ahk-copy-report-'));
  const log = new CommandLog(directory);
  t.after(() => {
    log.close();
    fs.rmSync(directory, { recursive: true, force: true });
  });
  await log.run('ui', 'refresh', async () => ({ outcome: 'Failed', code: 'AccessDenied' }));
  const state = context();
  const secret = 'secret-token-C:\\Users\\Private\\scripts.ahk';
  state.host!.error = secret;
  state.host!.snapshot!.scripts[0]!.path = secret;
  state.host!.snapshot!.scripts[0]!.friendlyName = secret;
  state.host!.snapshot!.settings.editorArguments = [secret];
  state.host!.snapshot!.issues = [{ code: 'StartupEntryConflict', detail: secret, scriptId: null }];
  state.host!.snapshot!.activity = [
    {
      id: 'id',
      timestamp: '2026-10-31T08:00:00.1234567-03:00',
      scriptId: null,
      operation: 'Start',
      outcome: 'Failed',
      code: 'AccessDenied',
      detail: secret,
    },
  ];
  state.updates!.error = secret;
  state.updates!.phase = 'error';
  const report = createDiagnosticReport(state, log, 'recent');
  assert.match(report, /AHK Manager diagnostic report/);
  assert.match(report, /1\.6\.0/);
  assert.match(report, /Administrator/);
  assert.match(report, /StartupEntryConflict/);
  assert.match(report, /2026-10-31T11:00:00\.123Z/);
  assert.match(report, /"launchManagerAtLogin": false/);
  assert.match(report, /"phase": "error"/);
  assert.match(report, /"command":"refresh"/);
  assert.doesNotMatch(report, /secret-token|Private|scripts\.ahk|editorArguments/);
});

test('recent errors omit cancelled Activity and report only matching command history', async (t) => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'ahk-copy-errors-'));
  const log = new CommandLog(directory);
  t.after(() => {
    log.close();
    fs.rmSync(directory, { recursive: true, force: true });
  });
  await log.run('ui', 'openLogs', () => null);
  await assert.rejects(
    log.run('ui', 'refresh', () => {
      throw Object.assign(new Error('private'), { code: 'EACCES' });
    }),
  );
  const state = context();
  state.host!.snapshot!.activity = [
    {
      id: 'id',
      timestamp: new Date().toISOString(),
      scriptId: null,
      operation: 'Stop',
      outcome: 'CancelledBeforeDispatch',
      code: 'None',
      detail: null,
    },
  ];
  const report = createDiagnosticReport(state, log, 'errors');
  assert.match(report, /Recent failures and refusals/);
  assert.match(report, /EACCES/);
  assert.doesNotMatch(report, /openLogs|CancelledBeforeDispatch|private/);
});

test('backend or log failures still produce a useful explicitly incomplete report', () => {
  const state = context();
  state.host = { ...state.host!, snapshot: null, connection: 'failed', error: 'must not leak' };
  const log = {
    snapshot: () => {
      throw new Error('private IO path');
    },
    status: () => ({
      available: false,
      directory: 'private path',
      failure: 'unsafe-path' as const,
    }),
  };
  const report = createDiagnosticReport(state, log, 'errors');
  assert.match(report, /"connection": "failed"/);
  assert.match(report, /"profileLoaded": false/);
  assert.match(report, /"readStatus": "unavailable"/);
  assert.match(report, /Command logs could not be read/);
  assert.doesNotMatch(report, /private|must not leak/);
  assert.match(createDiagnosticReport(state, null, 'recent'), /not-configured/);
});

test('Activity uses the newest twenty records from the backend newest-first history', () => {
  const state = context();
  state.host!.snapshot!.activity = Array.from({ length: 25 }, (_, index) => ({
    id: String(index),
    timestamp: new Date(Date.UTC(2026, 9, 31, 12, 59 - index)).toISOString(),
    scriptId: null,
    operation: 'Start',
    outcome: 'Failed',
    code: index === 0 ? 'StartupEntryConflict' : index === 24 ? 'MissingFile' : 'AccessDenied',
    detail: null,
  }));
  for (const mode of ['recent', 'errors'] as const) {
    const report = createDiagnosticReport(state, null, mode);
    assert.match(report, /StartupEntryConflict/);
    assert.doesNotMatch(report, /MissingFile/);
  }
});

test('reports enforce the UTF-8 clipboard limit without cutting a JSON record', () => {
  const records = Array.from({ length: 200 }, (_, index) => ({
    command: 'refresh',
    requestId: String(index),
    testPadding: 'ç'.repeat(900),
  }));
  const report = createDiagnosticReport(
    context(),
    {
      snapshot: () => ({ records, omittedRecords: 8, skippedFiles: 2 }),
      status: () => ({ available: true, directory: 'private', failure: null }),
    },
    'recent',
  );
  assert.ok(Buffer.byteLength(report, 'utf8') <= 65536);
  assert.match(report, /Files skipped: 2/);
  assert.doesNotMatch(report, /Older or excess matching records omitted: 8\./);
  const lines = report.split('\n').filter((line) => line.startsWith('{"command"'));
  assert.ok(lines.length > 0 && lines.length < 200);
  for (const line of lines) assert.doesNotThrow(() => JSON.parse(line));
  assert.equal(JSON.parse(lines.at(-1)!).requestId, '199');
});
