import test from 'node:test';
import assert from 'node:assert/strict';
import { shutdownForUpdate } from '../../src/main/updates/update-shutdown.js';
import type { ShutdownBoundary } from '../../src/main/lifecycle.js';

const tick = () => new Promise((resolve) => setImmediate(resolve));
function fixture() {
  const calls: string[] = [];
  let exited = false;
  const boundary: ShutdownBoundary = {
    drain: async () => {
      calls.push('drain');
    },
    backendPresent: () => true,
    backendExited: () => exited,
    persistPlacement: async () => {
      calls.push('placement');
    },
    shutdownBackend: async () => {
      calls.push('shutdown');
      exited = true;
    },
    reportPlacementFailure: () => {},
  };
  return { calls, boundary };
}
test('update shutdown confirms backend exit after draining and persisting placement', async () => {
  const f = fixture();
  await shutdownForUpdate(f.boundary, 1000);
  assert.deepEqual(f.calls, ['drain', 'placement', 'shutdown']);
});
test('a late drain after update deadline cannot persist placement or shut down the backend', async () => {
  const f = fixture();
  let release!: () => void, expire!: () => void;
  f.boundary.drain = () =>
    new Promise<void>((resolve) => {
      release = resolve;
    });
  const request = shutdownForUpdate(f.boundary, 1000, (fn) => {
    expire = fn;
    return () => {};
  });
  expire();
  await assert.rejects(request, /prazo/);
  release();
  await tick();
  assert.deepEqual(f.calls, []);
});
test('a late placement completion cannot close the backend after a cancelled update', async () => {
  const f = fixture();
  let release!: () => void, expire!: () => void;
  f.boundary.persistPlacement = () =>
    new Promise<void>((resolve) => {
      release = resolve;
    });
  const request = shutdownForUpdate(f.boundary, 1000, (fn) => {
    expire = fn;
    return () => {};
  });
  await tick();
  expire();
  await assert.rejects(request, /prazo/);
  release();
  await tick();
  assert.deepEqual(f.calls, ['drain']);
});
test('a shutdown acknowledgement without observed backend exit never permits applying an update', async () => {
  const f = fixture();
  f.boundary.shutdownBackend = async () => {
    f.calls.push('unconfirmed');
  };
  await assert.rejects(shutdownForUpdate(f.boundary, 1000), /não confirmou/);
});
