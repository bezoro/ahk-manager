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
