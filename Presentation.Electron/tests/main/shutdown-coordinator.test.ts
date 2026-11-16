import assert from 'node:assert/strict';
import test from 'node:test';
import { ShutdownCoordinator } from '../../src/main/desktop/shutdown-coordinator.js';
import { orderlyShutdown, shutdownWithinDeadline } from '../../src/main/lifecycle.js';

const tick = () => new Promise<void>((resolve) => setImmediate(resolve));
function deferred() {
  let release!: () => void;
  const wait = new Promise<void>((resolve) => {
    release = resolve;
  });
  return { wait, release };
}
const messages = { cancelled: 'cancelled', timedOut: 'timed out', unconfirmed: 'unconfirmed' };

function fixture() {
  const actions: string[] = [];
  const backend = {
    exited: false,
    async shutdown() {
      actions.push('shutdown');
      backend.exited = true;
    },
  };
  let owner: typeof backend | null = backend;
  const host = {
    backend: () => owner,
    drain: async () => {
      actions.push('drain');
    },
    persistPlacement: async (administrative: boolean) => {
      actions.push('persist:' + administrative);
    },
  };
  return {
    actions,
    backend,
    host,
    replace: (value: typeof owner) => {
      owner = value;
    },
  };
}

test('shutdown attempts preserve placement policy and observe the exact backend exit', async () => {
  for (const mode of ['standard', 'administrative', 'skip'] as const) {
    const f = fixture();
    const coordinator = new ShutdownCoordinator(f.host);
    const operation = coordinator.operation(mode);
    await shutdownWithinDeadline(operation.boundary, 1000, messages);
    assert.deepEqual(f.actions, [
      'drain',
      ...(mode === 'skip' ? [] : ['persist:' + (mode === 'administrative')]),
      'shutdown',
    ]);
    assert.equal(operation.dispatched, true);
    assert.equal(coordinator.inFlight, null);
    assert.equal(f.backend.exited, true);
  }
});

test('normal shutdown cannot save placement or dispatch after a drain deadline', async () => {
  const f = fixture();
  const blocked = deferred();
  f.host.drain = async () => {
    f.actions.push('drain');
    await blocked.wait;
  };
  const coordinator = new ShutdownCoordinator(f.host);
  const operation = coordinator.operation('standard');
  let expire!: () => void;
  const request = shutdownWithinDeadline(operation.boundary, 1000, {
    ...messages,
    schedule: (callback) => {
      expire = callback;
      return () => {};
    },
  });
  expire();
  await assert.rejects(request, /timed out/);
  blocked.release();
  await tick();
  assert.deepEqual(f.actions, ['drain']);
  assert.equal(operation.dispatched, false);
  assert.equal(coordinator.inFlight, null);
  assert.equal(f.backend.exited, false);
});

test('normal shutdown cannot dispatch after a late placement save passes its deadline', async () => {
  const f = fixture();
  const blocked = deferred();
  f.host.persistPlacement = async () => {
    f.actions.push('persist');
    await blocked.wait;
  };
  const coordinator = new ShutdownCoordinator(f.host);
  const operation = coordinator.operation('standard');
  let expire!: () => void;
  const request = shutdownWithinDeadline(operation.boundary, 1000, {
    ...messages,
    schedule: (callback) => {
      expire = callback;
      return () => {};
    },
  });
  await tick();
  expire();
  await assert.rejects(request, /timed out/);
  blocked.release();
  await tick();
  assert.deepEqual(f.actions, ['drain', 'persist']);
  assert.equal(operation.dispatched, false);
  assert.equal(f.backend.exited, false);
});

test('already dispatched shutdown stays observed after timeout and cannot be replayed', async () => {
  const f = fixture();
  const blocked = deferred();
  f.backend.shutdown = async () => {
    f.actions.push('shutdown');
    await blocked.wait;
    f.backend.exited = true;
  };
  const coordinator = new ShutdownCoordinator(f.host);
  const operation = coordinator.operation('standard');
  let expire!: () => void;
  const request = shutdownWithinDeadline(operation.boundary, 1000, {
    ...messages,
    schedule: (callback) => {
      expire = callback;
      return () => {};
    },
  });
  await tick();
  const pending = coordinator.inFlight;
  assert.ok(pending);
  assert.equal(operation.dispatched, true);
  expire();
  await assert.rejects(request, /timed out/);
  assert.equal(coordinator.inFlight, pending);
  await assert.rejects(
    coordinator.operation('standard').boundary.shutdownBackend(),
    /already been dispatched/,
  );
  blocked.release();
  await pending;
  assert.equal(coordinator.inFlight, null);
  assert.equal(operation.dispatched, true);
  assert.equal(f.actions.filter((value) => value === 'shutdown').length, 1);
});

test('a replaced backend cannot be persisted, stopped or mistaken for a completed owner', async () => {
  for (const phase of ['drain', 'persist'] as const) {
    const f = fixture();
    const blocked = deferred();
    if (phase === 'drain') f.host.drain = () => blocked.wait;
    else f.host.persistPlacement = () => blocked.wait;
    const coordinator = new ShutdownCoordinator(f.host);
    const operation = coordinator.operation('standard');
    const request = shutdownWithinDeadline(operation.boundary, 1000, messages);
    await tick();
    let replacementStopped = false;
    f.backend.exited = true;
    f.replace({
      exited: false,
      shutdown: async () => {
        replacementStopped = true;
      },
    });
    blocked.release();
    await assert.rejects(request, /backend changed/);
    assert.equal(operation.dispatched, false);
    assert.equal(replacementStopped, false);
  }
});

test('an absent or already exited backend needs no persistence or shutdown dispatch', async () => {
  for (const absent of [true, false]) {
    const f = fixture();
    if (absent) f.replace(null);
    else f.backend.exited = true;
    const operation = new ShutdownCoordinator(f.host).operation('standard');
    await orderlyShutdown(operation.boundary);
    assert.deepEqual(f.actions, ['drain']);
    assert.equal(operation.dispatched, false);
  }
});
