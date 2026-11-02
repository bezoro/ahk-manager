import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import { PortableInitializationError } from '../../src/main/desktop/storage-bootstrap.js';
import {
  recordStartupFailure,
  startupFailureDirectory,
  type StartupFailureContext,
} from '../../src/main/logging/startup-failure.js';
import { createDiagnosticArchive } from '../../src/main/logging/diagnostic-archive.js';
import { unzipSync } from 'fflate';

function fixture(t: { after(work: () => void): void }): {
  root: string;
  context: StartupFailureContext;
} {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'ahk-bootstrap-report-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  return {
    root,
    context: {
      localAppDataDirectory: path.join(root, 'Local'),
      version: '1.6.1',
      executablePath: path.join(root, 'Portable', 'App', 'Bezoro.AhkManager.exe'),
      layout: {
        mode: 'portable',
        applicationDirectory: path.join(root, 'Portable', 'App'),
        portableRoot: path.join(root, 'Portable'),
        launchExecutable: path.join(root, 'Portable', 'Bezoro.AhkManager.exe'),
        configurationPath: path.join(root, 'Portable', 'portable.json'),
        dataDirectory: path.join(root, 'Blocked Data'),
        profileDirectory: path.join(root, 'Blocked Data', 'Profile'),
        chromiumDirectory: path.join(root, 'Blocked Data', 'Chromium'),
        updatesDirectory: path.join(root, 'Blocked Data', 'Updates'),
        defaultDataDirectory: path.join(root, 'Portable', 'Data'),
        legacyDataDirectory: path.join(root, 'Legacy'),
      },
      login: false,
      background: false,
      error: new PortableInitializationError('access-denied'),
    },
  };
}

test('startup refusals persist independently without creating or touching blocked portable data', (t) => {
  const { context } = fixture(t);
  const result = recordStartupFailure(context);
  assert.ok(result.file);
  assert.equal(result.directory, startupFailureDirectory(context.localAppDataDirectory));
  assert.equal(fs.readFileSync(result.file, 'utf8'), result.text);
  assert.equal(fs.existsSync(context.layout!.dataDirectory), false);
  assert.equal(fs.existsSync(context.layout!.portableRoot!), false);
  const value = JSON.parse(result.text);
  assert.equal(value.version, '1.6.1');
  assert.equal(value.error.code, 'access-denied');
  assert.equal(value.error.diagnostic.origin, 'unknown');
  assert.match(result.summary, /Failure code: access-denied/);
});

test('background startup failure details are retained and unexpected exception payloads are excluded', (t) => {
  const { context } = fixture(t);
  context.login = true;
  context.background = true;
  context.error = Object.assign(new Error('private password or request payload'), {
    code: 'EACCES',
    requestBody: 'must not persist',
  });
  const result = recordStartupFailure(context);
  assert.ok(result.file);
  const value = JSON.parse(result.text);
  assert.deepEqual(value.startup, { login: true, background: true });
  assert.equal(value.error.code, 'EACCES');
  assert.doesNotMatch(result.text, /private password|request payload|must not persist|requestBody/);
});

test('unavailable report storage still returns copyable details and never replaces the startup failure', (t) => {
  const { root, context } = fixture(t);
  fs.writeFileSync(context.localAppDataDirectory, 'unrelated file');
  const result = recordStartupFailure(context);
  assert.equal(result.file, null);
  assert.equal(result.directory, null);
  assert.match(result.text, /access-denied/);
  assert.equal(fs.readFileSync(context.localAppDataDirectory, 'utf8'), 'unrelated file');
  assert.deepEqual(fs.readdirSync(root), ['Local']);
});

test('startup report retention bounds owned reports while preserving unrelated and changed formats', (t) => {
  const { context } = fixture(t);
  const first = recordStartupFailure(context);
  assert.ok(first.directory);
  const unrelated = path.join(first.directory, 'notes.json');
  const changed = path.join(
    first.directory,
    path
      .basename(first.file!)
      .replace(/-[0-9a-f-]{36}\.json$/, '-11111111-1111-1111-1111-111111111111.json'),
  );
  const value = JSON.parse(first.text);
  value.id = '11111111-1111-1111-1111-111111111111';
  value.userDocument = 'Preserve';
  fs.writeFileSync(unrelated, 'preserve');
  fs.writeFileSync(changed, JSON.stringify(value));
  for (let index = 0; index < 12; index++) assert.ok(recordStartupFailure(context).file);
  assert.equal(fs.readFileSync(unrelated, 'utf8'), 'preserve');
  assert.match(fs.readFileSync(changed, 'utf8'), /Preserve/);
  assert.equal(fs.readdirSync(first.directory).length, 12);
});

test('redirected per-user diagnostic paths are refused without writing through them', (t) => {
  const { root, context } = fixture(t);
  const external = path.join(root, 'External');
  fs.mkdirSync(external);
  fs.symlinkSync(
    external,
    context.localAppDataDirectory,
    process.platform === 'win32' ? 'junction' : 'dir',
  );
  const result = recordStartupFailure(context);
  assert.equal(result.file, null);
  assert.deepEqual(fs.readdirSync(external), []);
});

test('a later complete archive includes independent startup failures and detailed native context', (t) => {
  const { root, context } = fixture(t);
  const saved = recordStartupFailure(context);
  assert.ok(saved.directory);
  const profileDirectory = path.join(root, 'Profile');
  fs.mkdirSync(profileDirectory);
  const updatesDirectory = path.join(root, 'Updates');
  fs.mkdirSync(updatesDirectory);
  const archive = createDiagnosticArchive({
    deployment: { profileDirectory, updatesDirectory },
    summary: 'Summary',
    commandLogs: { files: [], issues: [] },
    startupDirectory: saved.directory,
  });
  assert.equal(archive.complete, true);
  const files = unzipSync(archive.bytes);
  const value = JSON.parse(Buffer.from(files['Startup/' + path.basename(saved.file!)]!).toString());
  assert.equal(value.error.code, 'access-denied');
  assert.equal(value.deployment.dataDirectory, context.layout!.dataDirectory);
});
