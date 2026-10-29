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
    'The update helper rejected the update. The installation has been preserved.',
  );
  assert.equal(
    updateHostRefusalMessage(readUpdateHostRefusal(Buffer.from(wire))),
    'The installation folder or its parent could not be verified as a private, trusted location. Check its permissions or install Manager in the default per-user folder. Nothing was changed.',
  );
  assert.equal(
    updateHostRefusalMessage({ schemaVersion: 1, phase: 'registration', code: 'invalid-data' }),
    'This copy of Manager is not registered for in-app updates. Run Manager Setup once, then open Manager from its installed shortcut. Your profile and scripts will be preserved.',
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
