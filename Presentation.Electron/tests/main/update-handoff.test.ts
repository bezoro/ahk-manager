import test, { type TestContext } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import fsSync from 'node:fs';
import childProcess, { type ChildProcess, type SpawnOptions } from 'node:child_process';
import { EventEmitter } from 'node:events';
import path from 'node:path';
import { createHash, randomUUID } from 'node:crypto';
import { prepareUpdate, preparedUpdateControl } from '../../src/main/updates/update-handoff.js';
import { UpdateStore } from '../../src/main/updates/update-store.js';
import { shutdownAndCommitUpdate } from '../../src/main/updates/update-shutdown.js';
import type { ShutdownBoundary } from '../../src/main/lifecycle.js';

async function fixture(t: TestContext) {
  const parent = process.env.AHK_DIAGNOSTICS_TEST_ROOT;
  assert.ok(
    parent && path.isAbsolute(parent),
    'The dispatcher must supply an isolated staging root.',
  );
  const root = await fs.mkdtemp(path.join(parent, 'update-handoff-'));
  t.after(async () => {
    await fs.rm(root, { recursive: true, force: true });
  });
  const store = new UpdateStore(root);
  const operationId = randomUUID(),
    parentStartedAtUtc = new Date().toISOString();
  const directory = path.join(root, 'operations', operationId);
  await fs.mkdir(directory, { recursive: true });
  const control = preparedUpdateControl(store, operationId, parentStartedAtUtc);
  const commit = path.join(directory, 'commit.json'),
    cancel = path.join(directory, 'cancel.json');
  return { store, control, directory, commit, cancel, operationId, parentStartedAtUtc };
}

test('the copied update host starts in its operation folder even when Manager is running from App', async (t) => {
  const f = await fixture(t);
  const store = new UpdateStore(path.join(f.store.root, 'Data', 'Updates'));
  const application = path.join(f.store.root, 'App');
  const helper = path.join(application, 'backend', 'update', 'AhkManager.UpdateHost.exe');
  await fs.mkdir(path.dirname(helper), { recursive: true });
  await fs.writeFile(helper, 'immutable helper fixture');
  const installerPath = path.join(store.root, 'installers', '1.6.0', 'installer.exe');
  const contents = Buffer.from('immutable installer fixture');
  await fs.mkdir(path.dirname(installerPath), { recursive: true });
  await fs.writeFile(installerPath, contents);
  const operationId = randomUUID();
  const directory = path.join(store.root, 'operations', operationId);
  let unref = 0;
  let dispatched = 0;
  const processFixture = Object.assign(new EventEmitter(), {
    stderr: null,
    exitCode: null,
    signalCode: null,
    unref: () => {
      unref++;
    },
  }) as unknown as ChildProcess;
  t.mock.method(childProcess, 'spawn', (command: string, args: string[], options: SpawnOptions) => {
    dispatched++;
    assert.equal(process.cwd(), application);
    assert.equal(options.cwd, directory);
    assert.equal(options.detached, true);
    assert.equal(options.shell, false);
    assert.notEqual(path.dirname(command), application);
    const planBytes = fsSync.readFileSync(args[1]!);
    assert.equal(args[3], createHash('sha256').update(planBytes).digest('hex'));
    const plan = JSON.parse(planBytes.toString('utf8'));
    fsSync.writeFileSync(
      path.join(directory, 'ready.json'),
      JSON.stringify({
        schemaVersion: 1,
        operationId: plan.operationId,
        parentPid: plan.parentPid,
        parentStartedAtUtc: plan.parentStartedAtUtc,
      }),
    );
    queueMicrotask(() => processFixture.emit('spawn'));
    return processFixture;
  });
  const original = process.cwd();
  try {
    process.chdir(application);
    const prepared = await prepareUpdate(
      store,
      operationId,
      {
        version: '1.6.0',
        installerPath,
        sha256: createHash('sha256').update(contents).digest('hex'),
        bytes: contents.length,
      },
      application,
      new Date().toISOString(),
    );
    assert.equal(dispatched, 1);
    assert.equal(unref, 1);
    await prepared.cancel();
    assert.equal(
      JSON.parse(await fs.readFile(path.join(directory, 'cancel.json'), 'utf8')).operationId,
      operationId,
    );
  } finally {
    process.chdir(original);
  }
});

test('shutdown commit is published complete, binds the exact session and has a single final link', async (t) => {
  const f = await fixture(t);
  let done = false;
  const writing = f.control.commit().finally(() => {
    done = true;
  });
  while (!done) {
    try {
      assert.equal(JSON.parse(await fs.readFile(f.commit, 'utf8')).operationId, f.operationId);
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code !== 'ENOENT') throw error;
    }
    await new Promise((resolve) => setImmediate(resolve));
  }
  await writing;
  assert.deepEqual(JSON.parse(await fs.readFile(f.commit, 'utf8')), {
    schemaVersion: 1,
    operationId: f.operationId,
    parentPid: process.pid,
    parentStartedAtUtc: f.parentStartedAtUtc,
  });
  assert.equal((await fs.stat(f.commit)).nlink, 1);
  assert.deepEqual(await fs.readdir(f.directory), ['commit.json']);
});

test('a published commit is immutable and simultaneous publishers cannot overwrite it', async (t) => {
  const f = await fixture(t);
  const attempts = await Promise.allSettled([f.control.commit(), f.control.commit()]);
  assert.equal(attempts.filter((item) => item.status === 'fulfilled').length, 1);
  const rejection = attempts.find((item) => item.status === 'rejected');
  assert.equal(rejection?.status === 'rejected' && rejection.reason.code, 'EEXIST');
  const bytes = await fs.readFile(f.commit);
  await assert.rejects(f.control.commit(), { code: 'EEXIST' });
  assert.deepEqual(await fs.readFile(f.commit), bytes);
  assert.equal((await fs.stat(f.commit)).nlink, 1);
});

test('cancellation is idempotent, prevents later commit and remains authoritative after a commit', async (t) => {
  const first = await fixture(t);
  await first.control.cancel();
  const original = await fs.readFile(first.cancel);
  await first.control.cancel();
  assert.deepEqual(await fs.readFile(first.cancel), original);
  await assert.rejects(first.control.commit(), /cancelled/);
  await assert.rejects(fs.access(first.commit), { code: 'ENOENT' });
  const second = await fixture(t);
  await second.control.commit();
  await second.control.cancel();
  assert.deepEqual(JSON.parse(await fs.readFile(second.cancel, 'utf8')), {
    schemaVersion: 1,
    operationId: second.operationId,
  });
  assert.equal((await fs.stat(second.commit)).nlink, 1);
});

test('the production shutdown sequence publishes no commit before confirmed backend exit', async (t) => {
  const f = await fixture(t);
  const calls: string[] = [];
  let exited = false;
  const boundary: ShutdownBoundary = {
    drain: async () => {
      calls.push('drain');
      await assert.rejects(fs.access(f.commit), { code: 'ENOENT' });
    },
    backendPresent: () => true,
    backendExited: () => exited,
    persistPlacement: async () => {
      calls.push('placement');
      await assert.rejects(fs.access(f.commit), { code: 'ENOENT' });
    },
    shutdownBackend: async () => {
      calls.push('backend-exit-0');
      exited = true;
    },
    reportPlacementFailure: () => {},
  };
  await shutdownAndCommitUpdate(boundary, 1000, async () => {
    await f.control.commit();
    calls.push('commit');
  });
  assert.deepEqual(calls, ['drain', 'placement', 'backend-exit-0', 'commit']);
  await fs.access(f.commit);
});

test('missing backend exit or shutdown failure cannot publish the shutdown commit', async (t) => {
  for (const scenario of ['unconfirmed', 'failed'] as const) {
    const f = await fixture(t);
    const boundary: ShutdownBoundary = {
      drain: async () => {},
      backendPresent: () => true,
      backendExited: () => false,
      persistPlacement: async () => {},
      shutdownBackend: async () => {
        if (scenario === 'failed') throw new Error('Shutdown failed');
      },
      reportPlacementFailure: () => {},
    };
    await assert.rejects(shutdownAndCommitUpdate(boundary, 1000, () => f.control.commit()));
    await f.control.cancel();
    await assert.rejects(fs.access(f.commit), { code: 'ENOENT' });
    await fs.access(f.cancel);
  }
});

test('a timed out drain cannot publish a late commit after cancellation', async (t) => {
  const f = await fixture(t);
  let release!: () => void, expire!: () => void;
  const boundary: ShutdownBoundary = {
    drain: () =>
      new Promise<void>((resolve) => {
        release = resolve;
      }),
    backendPresent: () => true,
    backendExited: () => false,
    persistPlacement: async () => {
      throw new Error('Late placement');
    },
    shutdownBackend: async () => {
      throw new Error('Late shutdown');
    },
    reportPlacementFailure: () => {},
  };
  const shutdown = shutdownAndCommitUpdate(
    boundary,
    1000,
    () => f.control.commit(),
    (callback) => {
      expire = callback;
      return () => {};
    },
  );
  expire();
  await assert.rejects(shutdown, /in time/);
  await f.control.cancel();
  release();
  await new Promise((resolve) => setImmediate(resolve));
  await assert.rejects(fs.access(f.commit), { code: 'ENOENT' });
  await fs.access(f.cancel);
});

test('a cancellation write failure does not invent a commit authorization', async (t) => {
  const f = await fixture(t);
  f.store.writeNew = async () => {
    throw Object.assign(new Error('Fixture cancellation IO failure'), { code: 'EACCES' });
  };
  await assert.rejects(f.control.cancel(), { code: 'EACCES' });
  await assert.rejects(fs.access(f.commit), { code: 'ENOENT' });
  await assert.rejects(fs.access(f.cancel), { code: 'ENOENT' });
});
