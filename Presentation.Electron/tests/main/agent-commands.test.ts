import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { PassThrough } from 'node:stream';
import test from 'node:test';
import { AgentCommands, failed, ok } from '../../src/main/agent-commands.js';
import { AgentReceipts } from '../../src/main/agent-receipts.js';
import {
  AgentFrameReader,
  MAX_AGENT_REQUEST,
  MAX_AGENT_RESPONSE,
  agentFrame,
  serveAgentTransport,
} from '../../src/main/agent-transport.js';
import { CommandRegistry } from '../../src/main/command-registry.js';
import { ManagerController, type NativeBoundary } from '../../src/main/controller.js';
import { desktopPreflight } from '../../src/main/lifecycle.js';
import {
  copyDataInventory,
  inventoryData,
  readDirectoryIdentity,
} from '../../src/main/storage/data-files.js';
import { UpdateStore } from '../../src/main/updates/update-store.js';
import type { OperationResult, SelectionContext } from '../../src/shared/contracts.js';
import { planId, runningSnapshot, selection } from './fixtures.js';

const confirmed: OperationResult = {
  outcome: 'Confirmed',
  code: 'None',
  detail: null,
  correlationId: null,
};
const requestId = randomUUID();
const agentRequest = (command: string, args: Record<string, unknown> = {}) => ({
  protocol: 1 as const,
  requestId,
  command,
  arguments: args,
});

test('a command review seals exact process identity without replacing the renderer selection', async () => {
  const registry = new CommandRegistry();
  const initial = runningSnapshot(2);
  let agent: AgentCommands;
  let performed: SelectionContext | undefined;
  const native: NativeBoundary = {
    files: async () => [],
    confirm: async (title, detail, target) =>
      agent.scope.getStore()?.confirm(title, detail, target) ?? true,
    alive: () => agent.scope.getStore()?.alive() ?? true,
    placement: () => initial.settings.window,
  };
  const controller = new ManagerController(
    {
      request: async <T>(command: string, payload: unknown): Promise<T> => {
        if (command === 'prepare') {
          const chosen = (payload as { selection: SelectionContext }).selection;
          const script = controller.state.snapshot!.scripts.find(
            (row) => row.id === chosen.scriptId,
          )!;
          return {
            planId,
            plan: {
              allowed: true,
              requiresConfirmation: true,
              expectedIdentity: script.process!.identity,
            },
          } as T;
        }
        assert.equal(command, 'perform');
        performed = (payload as { currentSelection: SelectionContext }).currentSelection;
        return confirmed as T;
      },
    },
    native,
    () => {},
  );
  controller.setSnapshot(initial);
  controller.setConnection('ready');
  const rendererSelection = {
    ...selection(),
    scriptId: initial.scripts[0]!.id,
    stateRevision: initial.revision,
  };
  controller.updateSelection(rendererSelection);
  agent = new AgentCommands({
    registry,
    controller,
    error: String,
    policy: () => ({
      quitting: false,
      updateApplying: false,
      adoptionPending: false,
      administrationPending: false,
    }),
    status: () => ({ ready: true, windowOpen: false }),
    ready: () => true,
    navigation: async () => ok(),
    shutdown: async () => ok(),
    cancelStopExit: () => ok(),
    checkAdoption: async () => ok(),
    continuation: async () => null,
    resumeStartup: async () => failed('OperationNotFound'),
    update: async () => ok(),
    updateLatest: async () => ok(),
    cancelUpdate: () => ok(),
    prepareStorage: async () => null,
    restartStorage: async () => ok(),
    report: () => '',
    exportDiagnostics: async () => null,
    receipt: async () => ok(),
  });
  const call = (name: string, args: Record<string, unknown> = {}) =>
    agent.dispatch(agentRequest(name, args), () => true);
  const cliRow = initial.scripts[1]!;
  const args = { scriptId: cliRow.id, revision: initial.revision, operation: 'Stop' };
  const review = async () => {
    const reply = await call('commands.preview', { command: 'scripts.operate', arguments: args });
    assert.equal(reply.outcome, 'Confirmed');
    return (reply.result as { confirmationToken: string }).confirmationToken;
  };
  assert.deepEqual((await call('app.status')).result, { ready: true, windowOpen: false });
  assert.equal((await call('app.status', { ignored: true })).code, 'InvalidArguments');
  assert.equal((await call('unregistered')).code, 'UnknownCommand');
  assert.equal((await call('scripts.operate', args)).code, 'ConfirmationRequired');
  const oldToken = await review();
  assert.equal(performed, undefined);
  cliRow.process!.identity.creationFileTime = '9007199254741000';
  assert.equal(
    (await call('scripts.operate', { ...args, confirmationToken: oldToken })).code,
    'ConfirmationChanged',
  );
  assert.equal(performed, undefined);
  const staleToken = await review();
  agent.invalidate();
  assert.equal(
    (await call('scripts.operate', { ...args, confirmationToken: staleToken })).code,
    'ConfirmationRequired',
  );
  const token = await review();
  assert.equal(
    (await call('scripts.operate', { ...args, confirmationToken: token })).outcome,
    'Confirmed',
  );
  assert.equal((performed as SelectionContext | undefined)?.scriptId, cliRow.id);
  assert.equal(
    (await call('scripts.operate', { ...args, confirmationToken: token })).code,
    'ConfirmationRequired',
  );
  assert.equal((await controller.operate('Stop', rendererSelection)).outcome, 'Confirmed');
  assert.equal((performed as SelectionContext | undefined)?.scriptId, rendererSelection.scriptId);
});

test('quiet preflight allows windowless readiness or a clean hidden current document', async () => {
  let dirty = false;
  let connected = true;
  let focused = false;
  const hidden = {
    alive: () => true,
    documentCurrent: () => connected,
    focused: () => focused,
    review: async (valid: () => boolean) => valid() && !dirty,
  };
  assert.equal(
    await desktopPreflight(
      () => true,
      () => true,
      undefined,
    ),
    true,
  );
  assert.equal(
    await desktopPreflight(
      () => false,
      () => true,
      undefined,
    ),
    false,
  );
  assert.equal(await desktopPreflight(() => true, undefined, undefined), false);
  assert.equal(
    await desktopPreflight(
      () => true,
      () => true,
      hidden,
    ),
    true,
  );
  assert.equal(await desktopPreflight(() => true, undefined, hidden), false);
  focused = true;
  assert.equal(await desktopPreflight(() => true, undefined, hidden), true);
  dirty = true;
  assert.equal(
    await desktopPreflight(
      () => true,
      () => true,
      hidden,
    ),
    false,
  );
  dirty = false;
  connected = false;
  assert.equal(
    await desktopPreflight(
      () => true,
      () => true,
      hidden,
    ),
    false,
  );
  assert.equal(
    await desktopPreflight(
      () => true,
      () => false,
      hidden,
    ),
    false,
  );
});

test('agent frame reader rejects invalid UTF-8, duplicate fields and oversized messages', () => {
  const reader = new AgentFrameReader();
  const values: unknown[] = [];
  const frame = agentFrame(agentRequest('app.status'));
  reader.push(frame.subarray(0, 2), (value) => values.push(value));
  reader.push(frame.subarray(2, 8), (value) => values.push(value));
  reader.push(frame.subarray(8), (value) => values.push(value));
  assert.deepEqual(values, [agentRequest('app.status')]);
  const large = Buffer.alloc(4);
  large.writeUInt32LE(MAX_AGENT_REQUEST + 1);
  assert.throws(() => reader.push(large, () => {}), /size/);
  assert.throws(() => agentFrame('x'.repeat(MAX_AGENT_RESPONSE)), /size/);
  const malformed = Buffer.from([1, 0, 0, 0, 0x80]);
  assert.throws(() => new AgentFrameReader().push(malformed, () => {}));
  const body = Buffer.from('{"protocol":1,"protocol":1}');
  const duplicate = Buffer.alloc(4);
  duplicate.writeUInt32LE(body.length);
  assert.throws(
    () => new AgentFrameReader().push(Buffer.concat([duplicate, body]), () => {}),
    /duplicate/,
  );
});

test('private cancellation invalidates preparation and only a matching delivery enables deferred effects', async () => {
  const input = new PassThrough();
  const output = new PassThrough();
  const reply = new AgentFrameReader();
  const frames: unknown[] = [];
  const effects: boolean[] = [];
  const failures: unknown[] = [];
  let release!: () => void;
  const hold = new Promise<void>((resolve) => {
    release = resolve;
  });
  const second = randomUUID();
  const drain = () => new Promise<void>((resolve) => setImmediate(resolve));
  output.on('data', (data: Buffer) => reply.push(data, (value) => frames.push(value)));
  const close = serveAgentTransport(
    input,
    output,
    { version: '1.2.1', generation: 'fixture' },
    async ({ command }, alive) => {
      if (command === 'prepare') await hold;
      return {
        ...ok({ alive: alive() }),
        afterReply: (delivered) => {
          effects.push(delivered && alive());
        },
      };
    },
    () => {},
    (error) => failures.push(error),
  );
  try {
    input.write(agentFrame(agentRequest('prepare')));
    input.write(agentFrame({ ...agentRequest('respond'), requestId: second }));
    await drain();
    assert.deepEqual(frames, [
      {
        protocol: 1,
        requestId: second,
        version: '1.2.1',
        generation: 'fixture',
        ...ok({ alive: true }),
      },
    ]);
    assert.deepEqual(effects, []);
    input.write(agentFrame({ protocol: 1, requestId, canceled: true }));
    input.write(agentFrame({ protocol: 1, requestId: second, delivered: true }));
    release();
    await drain();
    assert.deepEqual(effects, [true]);
    assert.deepEqual(frames.at(-1), {
      protocol: 1,
      requestId,
      version: '1.2.1',
      generation: 'fixture',
      ...ok({ alive: false }),
    });
    input.write(agentFrame({ protocol: 1, requestId, delivered: false }));
    await drain();
    assert.deepEqual(effects, [true, false]);
    assert.deepEqual(failures, []);
  } finally {
    close();
  }
});

test('acknowledging a request admits the next request in the same chunk at capacity', async () => {
  const input = new PassThrough();
  const output = new PassThrough();
  const reader = new AgentFrameReader();
  const responses: unknown[] = [];
  const completed: { requestId: string; delivered: boolean; alive: boolean }[] = [];
  const failures: unknown[] = [];
  const requests = Array.from({ length: 9 }, () => ({
    ...agentRequest('respond'),
    requestId: randomUUID(),
  }));
  const first = requests[0]!;
  const ninth = requests[8]!;
  const acknowledge = (requestId: string) =>
    agentFrame({ protocol: 1, requestId, delivered: true });
  const drain = () => new Promise<void>((resolve) => setImmediate(resolve));
  output.on('data', (chunk: Buffer) => reader.push(chunk, (value) => responses.push(value)));
  const close = serveAgentTransport(
    input,
    output,
    { version: '1.2.1', generation: 'fixture' },
    async ({ requestId }, alive) => ({
      ...ok(),
      afterReply: (delivered) => {
        completed.push({ requestId, delivered, alive: alive() });
      },
    }),
    () => {},
    (error) => failures.push(error),
  );
  try {
    input.write(Buffer.concat(requests.slice(0, 8).map(agentFrame)));
    await drain();
    assert.equal(responses.length, 8);

    input.write(Buffer.concat([acknowledge(first.requestId), agentFrame(ninth)]));
    await drain();
    assert.deepEqual(failures, []);
    assert.equal(responses.length, 9);
    assert.equal((responses.at(-1) as { requestId: string }).requestId, ninth.requestId);
    input.write(acknowledge(ninth.requestId));
    await drain();
    assert.deepEqual(
      completed,
      [first, ninth].map(({ requestId }) => ({
        requestId,
        delivered: true,
        alive: true,
      })),
    );
  } finally {
    close();
  }
});

test('a late receipt cannot enable a timed-out effect or disconnect the next request', async (t) => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const input = new PassThrough();
  const output = new PassThrough();
  const reader = new AgentFrameReader();
  const frames: unknown[] = [];
  const effects: boolean[] = [];
  const failures: unknown[] = [];
  const first = randomUUID();
  const second = randomUUID();
  const drain = () => new Promise<void>((resolve) => setImmediate(resolve));
  output.on('data', (data: Buffer) => reader.push(data, (value) => frames.push(value)));
  const close = serveAgentTransport(
    input,
    output,
    { version: '1.2.1', generation: 'fixture' },
    async (_request, alive) => ({
      ...ok({ alive: alive() }),
      afterReply: (delivered) => {
        effects.push(delivered && alive());
      },
    }),
    () => {},
    (error) => failures.push(error),
  );
  try {
    input.write(agentFrame({ ...agentRequest('first'), requestId: first }));
    await drain();
    assert.equal(frames.length, 1);
    t.mock.timers.tick(5_100);
    await drain();
    assert.deepEqual(effects, [false]);

    // The native side may finish writing its receipt just after the host times out.
    const late = agentFrame({ protocol: 1, requestId: first, delivered: true });
    input.write(late);
    input.write(agentFrame({ ...agentRequest('second'), requestId: second }));
    await drain();
    assert.deepEqual(failures, []);
    assert.deepEqual(frames.at(-1), {
      protocol: 1,
      requestId: second,
      version: '1.2.1',
      generation: 'fixture',
      ...ok({ alive: true }),
    });
    input.write(agentFrame({ protocol: 1, requestId: second, delivered: true }));
    await drain();
    assert.deepEqual(effects, [false, true]);

    input.write(late);
    assert.equal(failures.length, 1);
    assert.match(String(failures[0]), /Unexpected agent receipt/);
  } finally {
    close();
  }
});

test('a worker receipt requires the expected reopened version, and cancelled moves remain cancelled', async (t) => {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'ahk-agent-receipts-'));
  t.after(() => fs.rm(root, { recursive: true, force: true }));
  const store = new UpdateStore(root);
  const receipts = new AgentReceipts(store);
  const update = randomUUID();
  const current = {
    generation: 'old',
    version: '1.2.1',
    directory: root,
    ready: true,
    active: false,
  };
  await receipts.begin(update, current.generation, { kind: 'Update', version: '1.2.2' });
  assert.equal((await receipts.reconcile(update, current)).outcome, 'Unknown');
  assert.equal(
    (await receipts.reconcile(update, { ...current, active: true })).outcome,
    'Accepted',
  );
  await store.writeNew(
    path.join(root, 'operations', update, 'outcome.json'),
    JSON.stringify({
      schemaVersion: 1,
      operationId: update,
      outcome: 'Installed',
      installerExitCode: 0,
      version: '1.2.2',
    }),
  );
  assert.equal(
    (await receipts.reconcile(update, { ...current, version: '1.2.2' })).outcome,
    'Unknown',
  );
  assert.equal(
    (await receipts.reconcile(update, { ...current, version: '1.2.2', generation: 'new' })).outcome,
    'Confirmed',
  );
  const later = { ...current, version: '1.3.0', generation: 'later' };
  const restarted = new AgentReceipts(new UpdateStore(root));
  assert.equal((await restarted.reconcile(update, later)).outcome, 'Confirmed');
  const markerFile = path.join(root, 'agent-operations', update + '-confirmed', 'observation.json');
  const marker = await fs.readFile(markerFile, 'utf8');
  await fs.writeFile(
    markerFile,
    JSON.stringify({ ...JSON.parse(marker), generation: current.generation }),
  );
  assert.equal((await restarted.reconcile(update, later)).outcome, 'Unknown');
  await fs.writeFile(markerFile, marker);
  const workerFile = path.join(root, 'operations', update, 'outcome.json');
  const worker = JSON.parse(await fs.readFile(workerFile, 'utf8'));
  await fs.writeFile(workerFile, JSON.stringify({ ...worker, detail: 'different evidence' }));
  assert.equal((await restarted.reconcile(update, later)).outcome, 'Unknown');
  await fs.writeFile(workerFile, JSON.stringify({ ...worker, outcome: 'RolledBack' }));
  assert.equal((await restarted.reconcile(update, later)).outcome, 'Failed');
  await fs.rm(workerFile);
  assert.equal((await restarted.reconcile(update, later)).outcome, 'Unknown');
  const move = await receipts.begin(randomUUID(), current.generation, {
    kind: 'DataMove',
    moveId: randomUUID(),
    destination: root,
  });

  if (move.kind !== 'DataMove') assert.fail('Expected a data move receipt.');
  const reopened = { ...current, generation: 'new' };
  assert.equal((await receipts.reconcile(move.operationId, reopened)).outcome, 'Unknown');
  const retry = await receipts.begin(randomUUID(), current.generation, {
    kind: 'DataMove',
    moveId: move.moveId,
    destination: root,
  });
  if (retry.kind !== 'DataMove') assert.fail('Expected a data move receipt.');
  await receipts.markDataMoveDispatch(retry);
  assert.equal((await receipts.reconcile(retry.operationId, reopened)).outcome, 'Unknown');
  assert.equal((await receipts.reconcile(move.operationId, reopened)).outcome, 'Unknown');
  await receipts.settle(move, 'Cancelled');
  assert.equal(
    (await receipts.reconcile(move.operationId, { ...current, generation: 'new' })).code,
    'CancelledBeforeDispatch',
  );
});

test('a confirmed relocation keeps its history only while its dispatch and source evidence survive', async (t) => {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'ahk-move-history-'));
  t.after(() => fs.rm(root, { recursive: true, force: true }));
  const destination = path.join(root, 'First');
  const store = new UpdateStore(path.join(destination, 'Updates'));
  const receipts = new AgentReceipts(store);
  const id = randomUUID();
  const moveId = randomUUID();
  const receipt = await receipts.begin(id, 'before', { kind: 'DataMove', destination, moveId });
  assert.ok(receipt.kind === 'DataMove');
  await receipts.markDataMoveDispatch(receipt);
  const proof = {
    schemaVersion: 1,
    operationId: moveId,
    destination,
    identity: await readDirectoryIdentity(destination),
  };
  const relative = path.join('agent-operations', moveId + '-completed-data-move.json');
  await store.writeNew(path.join(store.root, relative), JSON.stringify(proof));
  const current = {
    generation: 'reopened',
    version: '1.2.2',
    directory: destination,
    ready: true,
    active: false,
  };
  assert.deepEqual(
    (await Promise.all([receipts.reconcile(id, current), receipts.reconcile(id, current)])).map(
      (reply) => reply.outcome,
    ),
    ['Confirmed', 'Confirmed'],
  );
  const next = path.join(root, 'Second');
  await fs.mkdir(next);
  await copyDataInventory(destination, next, await inventoryData(destination));
  await fs.rm(destination, { recursive: true });
  const moved = new AgentReceipts(new UpdateStore(path.join(next, 'Updates')));
  const later = { ...current, generation: 'later', directory: next };
  assert.equal((await moved.reconcile(id, later)).outcome, 'Confirmed');
  await fs.writeFile(
    path.join(next, 'Updates', relative),
    JSON.stringify({ ...proof, identity: { ...proof.identity, ino: '0' } }),
  );
  assert.equal((await moved.reconcile(id, later)).outcome, 'Unknown');
});

test('a live update confirmation survives failed publication and retries without replacing a competitor', async (t) => {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'ahk-confirm-publish-'));
  t.after(() => fs.rm(root, { recursive: true, force: true }));
  const store = new UpdateStore(root);
  const receipts = new AgentReceipts(store);
  const id = randomUUID();
  const version = '2.1.0';
  await receipts.begin(id, 'before', { kind: 'Update', version });
  await store.writeNew(
    path.join(root, 'operations', id, 'outcome.json'),
    JSON.stringify({
      schemaVersion: 1,
      operationId: id,
      version,
      outcome: 'Installed',
      installerExitCode: 0,
    }),
  );
  const live = { generation: 'reopened', version, directory: root, ready: true, active: false };
  const later = { ...live, generation: 'subsequent', version: '2.2.0' };
  const parent = path.join(root, 'agent-operations');
  const directory = path.join(parent, id + '-confirmed');
  const file = path.join(directory, 'observation.json');
  const refused = t.mock.method(fs, 'mkdtemp', async () => {
    throw Object.assign(new Error('Fixture storage refusal'), { code: 'EACCES' });
  });
  assert.equal((await receipts.reconcile(id, live)).outcome, 'Confirmed');
  assert.equal((await receipts.reconcile(id, later)).outcome, 'Unknown');
  refused.mock.restore();
  assert.equal((await receipts.reconcile(id, live)).outcome, 'Confirmed');
  assert.equal((await fs.lstat(file)).nlink, 1);
  assert.equal((await receipts.reconcile(id, later)).outcome, 'Confirmed');

  const conflicting = JSON.stringify({
    ...JSON.parse(await fs.readFile(file, 'utf8')),
    evidenceSha256: '0'.repeat(64),
  });
  await fs.rm(directory, { recursive: true });
  const rename = fs.rename;
  t.mock.method(fs, 'rename', async (...args: Parameters<typeof rename>) => {
    await fs.mkdir(directory);
    await fs.writeFile(file, conflicting);
    return rename(...args);
  });
  assert.equal((await receipts.reconcile(id, live)).outcome, 'Unknown');
  assert.equal(await fs.readFile(file, 'utf8'), conflicting);
  assert.deepEqual((await fs.readdir(parent)).sort(), [id + '-confirmed', id + '.json'].sort());
});
