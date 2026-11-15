import test, { type TestContext } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import {
  LogonDeploymentError,
  resolveStartupDeployment,
  type LogonDeploymentDiagnostic,
} from '../../src/main/desktop/logon-deployment.js';

function fixture(t: TestContext) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'ahk-logon-tests-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  const executable = path.join(root, 'App', 'Bezoro.AhkManager.exe');
  const marker = path.join(root, 'portable.json');
  const configuration = (dataDirectory = 'Data') =>
    JSON.stringify({
      schemaVersion: 1,
      applicationId: 'Bezoro.AhkManager.Electron',
      dataDirectory,
    });
  const argv = [executable, '--login', '--background'];
  const resolve = (
    timing: { now(): number; wait(ms: number): void },
    args = argv,
    packaged = true,
  ) => resolveStartupDeployment(executable, root, args, packaged, timing);
  return { root, executable, marker, argv, resolve, configuration };
}

const logs =
  ({ phase, reason, attempts, durationMs, exhausted }: LogonDeploymentDiagnostic) =>
  (error: unknown) => {
    assert.ok(error instanceof LogonDeploymentError);
    assert.deepEqual(error.diagnostic, { phase, reason, attempts, durationMs, exhausted });
    return true;
  };

function blockMarker(t: TestContext, marker: string, code: string): () => number {
  const original = fs.openSync;
  let calls = 0;
  t.mock.method(fs, 'openSync', (...args: Parameters<typeof original>) => {
    if (args[0] !== marker) return original(...args);
    calls++;
    throw Object.assign(new Error('private fixture payload'), { code, syscall: 'open' });
  });
  return () => calls;
}

const windows = { skip: process.platform !== 'win32' };

test(
  'registered logon retries a missing marker and accepts the fully validated new layout',
  windows,
  (t) => {
    const f = fixture(t);
    let time = 0;
    const result = f.resolve({
      now: () => time,
      wait: (ms) => {
        assert.equal(ms, 1_000);
        time += ms;
        fs.writeFileSync(f.marker, f.configuration('Recovered'));
      },
    });
    assert.equal(time, 1_000);
    assert.equal(result.dataDirectory, path.join(f.root, 'Recovered'));
  },
);

test('a verified storage snapshot change triggers a complete new validation', windows, (t) => {
  const f = fixture(t);
  fs.writeFileSync(f.marker, f.configuration());
  const original = fs.fstatSync;
  let calls = 0;
  t.mock.method(fs, 'fstatSync', (descriptor: number) => {
    const stamp = original(descriptor, { bigint: true });
    if (++calls === 1) stamp.ino += 1n;
    return stamp;
  });
  let time = 0;
  const result = f.resolve({
    now: () => time,
    wait: (ms) => {
      time += ms;
    },
  });
  assert.equal(time, 1_000);
  assert.equal(calls, 3);
  assert.equal(result.configurationPath, f.marker);
});

test(
  'a busy marker is tried at most 45 times and no attempt begins after a late wake',
  windows,
  (t) => {
    const f = fixture(t);
    fs.writeFileSync(f.marker, f.configuration());
    const calls = blockMarker(t, f.marker, 'EBUSY');
    let time = 0;
    assert.throws(
      () =>
        f.resolve({
          now: () => time,
          wait: (ms) => {
            assert.equal(ms, 1_000);
            time += ms;
          },
        }),
      logs({
        phase: 'PortableMarker',
        reason: 'ResourceBusy',
        attempts: 45,
        durationMs: 44_000,
        exhausted: true,
      }),
    );
    assert.equal(calls(), 45);
    time = 0;
    assert.throws(
      () =>
        f.resolve({
          now: () => time,
          wait: () => {
            time = 45_001;
          },
        }),
      logs({
        phase: 'PortableMarker',
        reason: 'ResourceBusy',
        attempts: 1,
        durationMs: 45_001,
        exhausted: true,
      }),
    );
    assert.equal(calls(), 46);
  },
);

test('rejected access and unknown I/O stop without waiting', windows, (t) => {
  const f = fixture(t);
  fs.writeFileSync(f.marker, f.configuration());
  const timing = { now: () => 0, wait: () => assert.fail('permanent failures must not wait') };
  for (const [code, reason] of [
    ['EACCES', 'AccessDenied'],
    ['EPERM', 'AccessDenied'],
    ['EIO', 'IoFailure'],
  ] as const) {
    const calls = blockMarker(t, f.marker, code);
    assert.throws(
      () => f.resolve(timing),
      logs({ phase: 'PortableMarker', reason, attempts: 1, durationMs: 0, exhausted: false }),
    );
    assert.equal(calls(), 1);
    t.mock.restoreAll();
  }
});

test('invalid marker type, content, path and redirection fail before waiting', windows, (t) => {
  const f = fixture(t);
  const timing = { now: () => 0, wait: () => assert.fail('invalid data must not wait') };
  const invalid = (
    phase: LogonDeploymentDiagnostic['phase'],
    reason: LogonDeploymentDiagnostic['reason'],
  ) =>
    assert.throws(
      () => f.resolve(timing),
      logs({ phase, reason, attempts: 1, durationMs: 0, exhausted: false }),
    );
  fs.mkdirSync(f.marker);
  invalid('PortableMarker', 'InvalidConfiguration');
  fs.rmdirSync(f.marker);
  fs.writeFileSync(f.marker, '{"schemaVersion":1,"schemaVersion":1}');
  invalid('PortableMarker', 'InvalidConfiguration');
  fs.writeFileSync(f.marker, f.configuration('../Ambiguous'));
  invalid('DataDirectory', 'InvalidPath');
  const actual = path.join(f.root, 'Actual');
  fs.mkdirSync(actual);
  fs.symlinkSync(actual, path.join(f.root, 'Redirect'), 'junction');
  fs.writeFileSync(f.marker, f.configuration('Redirect'));
  invalid('DataDirectory', 'InvalidPath');
});

test(
  'other startup arguments and unpackaged runs retain the original single attempt',
  windows,
  (t) => {
    const f = fixture(t);
    fs.writeFileSync(f.marker, f.configuration());
    const calls = blockMarker(t, f.marker, 'EBUSY');
    const timing = {
      now: () => assert.fail('ineligible startup must not initialize timing'),
      wait: () => assert.fail('ineligible startup must not wait'),
    };
    for (const [argv, packaged] of [
      [f.argv, false],
      [[f.executable], true],
      [[f.executable, '--agent-host'], true],
      [[f.executable, '--login'], true],
      [[f.executable, '--background', '--login'], true],
      [[...f.argv, '--foreground'], true],
      [[...f.argv, '--login'], true],
      [[path.join(f.root, 'Other.exe'), '--login', '--background'], true],
    ] as const)
      assert.throws(() => f.resolve(timing, [...argv], packaged), { code: 'EBUSY' });
    assert.equal(calls(), 8);
  },
);
