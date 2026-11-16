import assert from 'node:assert/strict';
import test from 'node:test';
import type { AgentScope } from '../../src/main/agent-commands.js';
import { AgentTransitionCoordinator } from '../../src/main/agent-transition-coordinator.js';
import type { AgentReceipt } from '../../src/main/agent-receipts.js';

type Host = ConstructorParameters<typeof AgentTransitionCoordinator>[0];
const operationId = '42eca1f3-8051-4e89-9fd6-3f0ce4f88629';
const generation = '538498f5-e954-44de-a16b-0ba4aca25b5a';
const update = { kind: 'Update' as const, version: '2.1.0' };

function fixture() {
  const events: string[] = [];
  const settled: Array<{ receipt: AgentReceipt; outcome: string; detail?: string }> = [];
  const state = { ready: true, alive: true, current: true, uncertain: false, owner: {} };
  const scope: AgentScope = { alive: () => state.alive, files: [], confirm: async () => true };
  let dispatchedScope: AgentScope | undefined;
  const receipts: NonNullable<ReturnType<Host['receipts']>> = {
    begin: async (id, source, transition) => {
      events.push('begin');
      return { schemaVersion: 1, operationId: id, generation: source, ...transition };
    },
    markDataMoveDispatch: async () => {
      events.push('mark-data-dispatch');
    },
    settle: async (receipt, outcome, detail) => {
      events.push('settle:' + outcome);
      settled.push({ receipt, outcome, detail });
    },
  };
  const host: Host = {
    generation,
    scope: () => scope,
    ready: () => state.ready,
    owner: () => state.owner,
    current: (owner) => state.current && state.owner === owner,
    receipts: () => receipts,
    review: async (_id, eligible) => {
      events.push('review');
      return eligible();
    },
    run: async (scope, work) => {
      dispatchedScope = scope;
      return work();
    },
    uncertain: () => state.uncertain,
    error: () => 'safe failure detail',
    report: () => {
      events.push('report');
    },
    resume: () => {
      events.push('resume');
    },
  };
  return { state, events, settled, scope, receipts, host, dispatchedScope: () => dispatchedScope };
}

test('agent transitions require reply delivery before work and retain successful dispatch authority', async () => {
  const f = fixture();
  const coordinator = new AgentTransitionCoordinator(f.host);
  let dispatches = 0;
  const reply = await coordinator.reserve(
    operationId,
    update,
    async () => {
      dispatches++;
      return 'Dispatched';
    },
    'reviewed-release',
  );
  assert.equal(reply.outcome, 'Accepted');
  assert.equal(coordinator.active, operationId);
  assert.equal(dispatches, 0);
  assert.deepEqual(f.events, ['review', 'begin']);
  await reply.afterReply!(true);
  await reply.afterReply!(true);
  assert.equal(dispatches, 1);
  assert.equal(coordinator.active, operationId);
  assert.equal(f.settled.length, 0);
  assert.equal(f.dispatchedScope()?.transition, operationId);
  assert.equal(f.dispatchedScope()?.updateRelease, 'reviewed-release');
  assert.equal(f.dispatchedScope()?.alive(), true);
  f.state.current = false;
  assert.equal(f.dispatchedScope()?.alive(), false);
});

test('undelivered, expired or replaced-owner replies cancel without dispatch and release the reservation', async () => {
  for (const reason of ['undelivered', 'expired', 'replaced'] as const) {
    const f = fixture();
    const coordinator = new AgentTransitionCoordinator(f.host);
    let dispatched = false;
    const reply = await coordinator.reserve(operationId, update, async () => {
      dispatched = true;
      return 'Dispatched';
    });
    if (reason === 'expired') f.state.alive = false;
    if (reason === 'replaced') f.state.owner = {};
    await reply.afterReply!(reason !== 'undelivered');
    assert.equal(dispatched, false);
    assert.equal(coordinator.active, null);
    assert.deepEqual(
      f.settled.map((entry) => entry.outcome),
      ['Cancelled'],
    );
    assert.equal(f.events.at(-1), 'resume');
  }
});

test('readiness, preflight and publication failures cannot leave an agent reservation behind', async () => {
  const f = fixture();
  const coordinator = new AgentTransitionCoordinator(f.host);
  const work = async () => 'Dispatched' as const;
  f.state.ready = false;
  assert.equal((await coordinator.reserve(operationId, update, work)).code, 'OperationBlocked');
  f.state.ready = true;
  f.host.review = async () => {
    f.state.owner = {};
    return true;
  };
  assert.equal((await coordinator.reserve(operationId, update, work)).code, 'PreflightBlocked');
  f.host.review = async () => true;
  f.receipts.begin = async () => {
    throw new Error('publication failed');
  };
  await assert.rejects(coordinator.reserve(operationId, update, work), /publication failed/);
  assert.equal(coordinator.active, null);
});

test('one active agent transition suppresses competing admission', async () => {
  const f = fixture();
  const coordinator = new AgentTransitionCoordinator(f.host);
  const reply = await coordinator.reserve(operationId, update, async () => 'Dispatched');
  assert.equal(
    (await coordinator.reserve(generation, update, async () => 'Dispatched')).code,
    'OperationBlocked',
  );
  await reply.afterReply!(false);
  assert.equal(coordinator.active, null);
});

test('data-move dispatch evidence is durable before transition work begins', async () => {
  const f = fixture();
  const coordinator = new AgentTransitionCoordinator(f.host);
  const reply = await coordinator.reserve(
    operationId,
    {
      kind: 'DataMove',
      moveId: generation,
      destination: 'C:\\ManagerData',
    },
    async () => {
      f.events.push('work');
      return 'Dispatched';
    },
  );
  await reply.afterReply!(true);
  assert.ok(f.events.indexOf('mark-data-dispatch') < f.events.indexOf('work'));
  assert.equal(coordinator.active, operationId);
});

test('transition failures preserve unknown outcomes after authority loss without replay', async () => {
  for (const uncertain of [false, true]) {
    const f = fixture();
    f.state.uncertain = uncertain;
    const coordinator = new AgentTransitionCoordinator(f.host);
    let attempts = 0;
    const reply = await coordinator.reserve(operationId, update, async () => {
      attempts++;
      throw new Error('original unsafe diagnostic');
    });
    await reply.afterReply!(true);
    await reply.afterReply!(true);
    assert.equal(attempts, 1);
    assert.deepEqual(
      f.settled.map(({ outcome, detail }) => ({ outcome, detail })),
      [{ outcome: uncertain ? 'Unknown' : 'Failed', detail: 'safe failure detail' }],
    );
    assert.equal(coordinator.active, null);
  }
});

test('settlement failure still releases admission and reports evidence without retrying the effect', async () => {
  const f = fixture();
  f.receipts.settle = async () => {
    throw new Error('receipt unavailable');
  };
  const coordinator = new AgentTransitionCoordinator(f.host);
  const reply = await coordinator.reserve(operationId, update, async () => 'Failed');
  await reply.afterReply!(true);
  assert.equal(coordinator.active, null);
  assert.deepEqual(f.events.slice(-2), ['report', 'resume']);
});
