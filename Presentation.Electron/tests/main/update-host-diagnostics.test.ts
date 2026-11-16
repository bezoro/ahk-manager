import test from 'node:test';
import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import { PassThrough } from 'node:stream';
import type { ChildProcess } from 'node:child_process';
import {
  observeUpdateHost,
  readUpdateHostRefusal,
  updateHostRefusalMessage,
} from '../../src/main/updates/update-host-diagnostics.js';

const wire =
  'AHK_UPDATE_REFUSAL:{"schemaVersion":1,"phase":"installation-parent-ownership","code":"access-denied"}\n';
const expected = {
  schemaVersion: 1,
  phase: 'installation-parent-ownership',
  code: 'access-denied',
};

function fixture() {
  const child = new EventEmitter() as EventEmitter & {
    stderr: PassThrough & { unref: () => void };
  };
  child.stderr = new PassThrough() as typeof child.stderr;
  let unreferenced = false;
  child.stderr.unref = () => {
    unreferenced = true;
  };
  const observer = observeUpdateHost(child as unknown as ChildProcess);
  return { child, observer, unreferenced: () => unreferenced };
}

test('native refusal diagnostics accept only complete records from the fixed vocabulary', () => {
  assert.deepEqual(readUpdateHostRefusal(Buffer.from(wire)), expected);
  assert.deepEqual(readUpdateHostRefusal(Buffer.from(wire.replace('\n', '\r\n'))), expected);
  assert.deepEqual(readUpdateHostRefusal(Buffer.from('untrusted startup text\n' + wire)), expected);
  for (const invalid of [
    wire.trimEnd(),
    wire.replace('schemaVersion":1', 'schemaVersion":2'),
    wire.replace('"phase":', '"extra":true,"phase":'),
    wire.replace('"phase":', '"schemaVersion":1,"phase":'),
    wire.replace('access-denied', 'C:\\Users\\private\\secret'),
    wire.replace('installation-parent-ownership', 'unknown-phase'),
    wire.replace('access-denied', 'unknown-code'),
    wire.replace('access-denied', 'access-denied\u0000'),
    wire + wire,
    wire + wire.trimEnd(),
    wire + 'AHK_UPDATE_REFUSAL:malformed\n',
    'x'.repeat(4096) + wire,
  ]) {
    assert.equal(readUpdateHostRefusal(Buffer.from(invalid)), null);
  }
  const highBit = Buffer.from(wire);
  highBit[highBit.indexOf('access-denied')]! |= 0x80;
  assert.equal(readUpdateHostRefusal(highBit), null);
});

test('administrator-required is a finite native refusal with root-launcher recovery instructions', () => {
  const native =
    'AHK_UPDATE_REFUSAL:{"schemaVersion":1,"phase":"portable-initialization","code":"administrator-required"}\n';
  const value = readUpdateHostRefusal(Buffer.from(native));
  assert.deepEqual(value, {
    schemaVersion: 1,
    phase: 'portable-initialization',
    code: 'administrator-required',
  });
  assert.match(
    updateHostRefusalMessage(value),
    /requires administrator access.*root launcher.*Bezoro\.AhkManager\.exe.*Windows UAC/,
  );
  for (const invalid of [
    native.trimEnd(),
    native + native,
    native.replace('administrator-required', 'administrator-required-private'),
    native.replace('"code":', '"token":"private-value","code":'),
    'x'.repeat(4096) + native,
  ])
    assert.equal(readUpdateHostRefusal(Buffer.from(invalid)), null);
});

test('stderr is drained immediately across split chunks and after process exit before stream close', async () => {
  const f = fixture();
  assert.equal(f.unreferenced(), true, 'the pipe must not prevent normal Manager exit');
  f.child.stderr.write(wire.slice(0, 33));
  f.child.emit('exit', 65, null);
  const reading = f.observer.refusal();
  await new Promise<void>((resolve) => setImmediate(resolve));
  f.child.stderr.end(wire.slice(33));
  f.child.emit('close', 65, null);
  assert.deepEqual(await reading, expected);
});

test('overflow is discarded permanently while the remaining stderr is still drained', async () => {
  const f = fixture();
  f.child.stderr.write(wire);
  f.child.stderr.write(Buffer.alloc(8192, 120));
  f.child.stderr.write(wire);
  f.child.stderr.end();
  f.child.emit('close', 65, null);
  assert.equal(await f.observer.refusal(), null);
  assert.equal(f.child.stderr.readableLength, 0);
});

test('a broken stderr stream cannot replace the preparation failure or supply partial diagnostics', async () => {
  const f = fixture();
  f.child.stderr.write(wire);
  f.child.stderr.emit('error', new Error('Untrusted error text and path'));
  f.child.emit('close', 65, null);
  assert.equal(await f.observer.refusal(), null);
});

test('diagnostic collection is bounded when an inherited pipe outlives the rejected helper', async () => {
  const f = fixture();
  f.child.stderr.write(wire);
  const pending = f.observer.refusal();
  assert.deepEqual(await pending, expected);
  f.child.stderr.end();
  f.child.emit('close', 65, null);
});

test('preparation refusals explain supported recovery without exposing native diagnostics', () => {
  assert.equal(
    updateHostRefusalMessage(null),
    'The update helper rejected the update. The portable application has been preserved.',
  );
  assert.equal(
    updateHostRefusalMessage(readUpdateHostRefusal(Buffer.from(wire))),
    'The portable folder or its parent could not be verified as a private, trusted location. Check its permissions or move Manager to a private NTFS folder. Nothing was changed.',
  );
  assert.equal(
    updateHostRefusalMessage({ schemaVersion: 1, phase: 'registration', code: 'invalid-data' }),
    updateHostRefusalMessage(null),
  );
  assert.equal(
    updateHostRefusalMessage({ schemaVersion: 1, phase: 'registration', code: 'access-denied' }),
    updateHostRefusalMessage(null),
    'an unreadable registration must not be presented as absent',
  );
  assert.equal(
    updateHostRefusalMessage({ schemaVersion: 1, phase: 'private path', code: 'secret text' }),
    updateHostRefusalMessage(null),
  );
});

test('storage refusal codes explain NTFS and cloud availability before generic ownership guidance', () => {
  for (const phase of ['cache-ownership', 'installation-ownership', 'portable-initialization']) {
    const ntfs = updateHostRefusalMessage({
      schemaVersion: 1,
      phase,
      code: 'unsupported-filesystem',
    });
    assert.match(ntfs, /require NTFS.*ReFS, FAT32 and exFAT/);
    assert.doesNotMatch(ntfs, /permissions|Setup/);
    assert.match(
      updateHostRefusalMessage({ schemaVersion: 1, phase, code: 'cloud-file-unavailable' }),
      /available offline.*sync app.*syncing/,
    );
    assert.match(
      updateHostRefusalMessage({ schemaVersion: 1, phase, code: 'redirected-path' }),
      /symbolic link, junction or unsupported redirect/,
    );
  }
});

test('installed-image refusals preserve the native phase and safe reason before readiness', () => {
  const native =
    'AHK_UPDATE_REFUSAL:{"schemaVersion":1,"phase":"installed-image","code":"invalid-data"}\n';
  assert.deepEqual(readUpdateHostRefusal(Buffer.from(native)), {
    schemaVersion: 1,
    phase: 'installed-image',
    code: 'invalid-data',
  });
});

test('the native contract verifier runs the production parser and refuses unknown or malformed native records', async (t) => {
  const { default: fs } = await import('node:fs');
  const { default: os } = await import('node:os');
  const { default: path } = await import('node:path');
  const { spawnSync } = await import('node:child_process');
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'ahk-native-contract-'));
  t.after(() => fs.rmSync(directory, { recursive: true, force: true }));
  const file = path.join(directory, 'native.json');
  const run = () =>
    spawnSync(
      process.execPath,
      [
        '--experimental-strip-types',
        path.resolve('scripts/verify-update-host-diagnostics.cjs'),
        '--input',
        file,
      ],
      { encoding: 'utf8', shell: false },
    );
  const line =
    'AHK_UPDATE_REFUSAL:{"schemaVersion":1,"phase":"installed-image","code":"invalid-data"}';
  fs.writeFileSync(file, JSON.stringify([line, line.replace('installed-image', 'readiness')]));
  const valid = run();
  assert.equal(valid.status, 0, valid.stderr);
  assert.match(valid.stdout, /2 phases, 1 codes, 2 emitted records/);
  for (const fixture of [
    [line.replace('installed-image', 'future-unreviewed-phase')],
    [line.replace('invalid-data', 'private-unreviewed-code')],
    [line.replace('"phase":', '"schemaVersion":1,"phase":')],
    [],
    ['x'.repeat(513)],
    { records: [line] },
  ]) {
    fs.writeFileSync(file, JSON.stringify(fixture));
    assert.notEqual(run().status, 0);
  }
  fs.writeFileSync(file, 'x'.repeat(256 * 1024 + 1));
  assert.notEqual(run().status, 0);
});
