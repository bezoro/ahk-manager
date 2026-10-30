import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { createCommandLog } from '../../src/main/logging/create-command-log.js';

test('installed startup diagnostics retain early commands and failure without populating the migration destination', async (t) => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'ahk-installed-log-'));
  const profile = path.join(root, 'Bezoro', 'AhkManagerElectron');
  fs.mkdirSync(profile, { recursive: true });
  const log = createCommandLog({ mode: 'installed', dataDirectory: profile }, root);
  t.after(() => {
    log.close();
    fs.rmSync(root, { recursive: true, force: true });
  });
  assert.equal(log.status().available, true);
  assert.equal(log.directory, path.join(root, 'Bezoro', 'AhkManagerElectronDiagnostics', 'Logs'));
  assert.deepEqual(fs.readdirSync(profile), []);
  let starts = 0;
  const error = Object.assign(new Error('private startup error'), { code: 'ENOENT' });
  await assert.rejects(
    log.run('backend', 'start', () => {
      starts++;
      throw error;
    }),
    (value) => value === error,
  );
  for (const command of ['hello', 'initializeProfile', 'migrate', 'load'])
    await log.run('backend', command, () => ({ outcome: 'Confirmed', code: 'None' }));
  assert.equal(starts, 1);
  assert.equal(log.status().available, true, 'Help can still open the initialized log folder.');
  assert.deepEqual(fs.readdirSync(profile), []);
  log.close();
  const text = fs
    .readdirSync(log.directory)
    .map((file) => fs.readFileSync(path.join(log.directory, file), 'utf8'))
    .join('');
  const records = text
    .trim()
    .split('\n')
    .map((line) => JSON.parse(line));
  assert.ok(
    records.some(
      (record) =>
        record.command === 'start' && record.phase === 'error' && record.errorCode === 'ENOENT',
    ),
  );
  for (const command of ['hello', 'initializeProfile', 'migrate', 'load'])
    assert.equal(
      records.filter((record) => record.command === command && record.phase === 'completed').length,
      1,
    );
  assert.doesNotMatch(text, /private startup error/);
});

test('portable logging stays with Data and leaves a fresh profile empty', (t) => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'ahk-portable-log-'));
  const data = path.join(root, 'Data');
  const profile = path.join(data, 'Profile');
  fs.mkdirSync(profile, { recursive: true });
  const log = createCommandLog({ mode: 'portable', dataDirectory: data }, root);
  t.after(() => {
    log.close();
    fs.rmSync(root, { recursive: true, force: true });
  });
  assert.equal(log.directory, path.join(data, 'Logs'));
  assert.equal(log.status().available, true);
  assert.deepEqual(fs.readdirSync(profile), []);
});

test('installed startup diagnostics reject redirected ancestors and preserve unrelated files without blocking commands', async (t) => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'ahk-installed-log-unsafe-'));
  const outside = path.join(root, 'external');
  const local = path.join(root, 'Local');
  fs.mkdirSync(outside);
  fs.mkdirSync(local);
  const external = path.join(outside, 'untouched.txt');
  fs.writeFileSync(external, 'preserve these bytes');
  fs.symlinkSync(
    outside,
    path.join(local, 'Bezoro'),
    process.platform === 'win32' ? 'junction' : 'dir',
  );
  const log = createCommandLog(
    { mode: 'installed', dataDirectory: path.join(local, 'Profile') },
    local,
  );
  t.after(() => {
    log.close();
    fs.rmSync(root, { recursive: true, force: true });
  });
  assert.equal(log.status().available, false);
  assert.equal(await log.run('backend', 'load', () => 42), 42);
  assert.deepEqual(fs.readdirSync(outside), ['untouched.txt']);
  assert.equal(fs.readFileSync(external, 'utf8'), 'preserve these bytes');
});
