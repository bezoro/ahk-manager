import test from 'node:test';
import assert from 'node:assert/strict';
import { AsyncLocalStorage } from 'node:async_hooks';
import type { IpcMainEvent, IpcMainInvokeEvent, WebContents } from 'electron';
import {
  registerHandlers,
  type IpcContext,
  type IpcTransport,
} from '../../src/main/ipc/register-handlers.js';
import { type RequestPolicy } from '../../src/main/ipc/request-policy.js';
import { APP_URL, DocumentGuard } from '../../src/main/security.js';
import { AdministrationPreflight, ElevationCoordinator } from '../../src/main/elevation.js';
import { ManagerController } from '../../src/main/controller.js';
import type { ElevationStatus, OperationResult } from '../../src/shared/contracts.js';
import { emptyUpdateState, type UpdateChannel } from '../../src/shared/updates.js';
import { snapshot } from './fixtures.js';

class RecordedIpc implements IpcTransport {
  readonly handlers = new Map<string, (event: IpcMainInvokeEvent, envelope: unknown) => unknown>();
  readonly notifications = new Map<string, (event: IpcMainEvent, envelope: unknown) => void>();
  handle(
    channel: string,
    listener: (event: IpcMainInvokeEvent, envelope: unknown) => unknown,
  ): void {
    assert.equal(this.handlers.has(channel), false);
    this.handlers.set(channel, listener);
  }
  on(channel: string, listener: (event: IpcMainEvent, envelope: unknown) => void): void {
    assert.equal(this.notifications.has(channel), false);
    this.notifications.set(channel, listener);
  }
  async invoke(channel: string, event: IpcMainInvokeEvent, envelope: unknown): Promise<unknown> {
    const handler = this.handlers.get(channel);
    assert.ok(handler, `Unregistered handler ${channel}`);
    return handler(event, envelope);
  }
}

const confirmed: OperationResult = {
  outcome: 'Confirmed',
  code: 'None',
  detail: null,
  correlationId: null,
};

function fixture() {
  const frame = { url: APP_URL, processId: 10, routingId: 20, isDestroyed: () => false };
  const contents = { mainFrame: frame, isDestroyed: () => false };
  const event = { sender: contents, senderFrame: frame } as unknown as IpcMainInvokeEvent;
  const guard = new DocumentGuard(contents as unknown as WebContents);
  const ipc = new RecordedIpc(),
    copied: string[] = [];
  const documentScope = new AsyncLocalStorage<() => boolean>();
  const controller = new ManagerController(
    {
      request: async () => {
        throw new Error('No backend effect is permitted by this fixture');
      },
    },
    {
      files: async () => [],
      confirm: async () => false,
      alive: () => documentScope.getStore()?.() ?? true,
      placement: () => snapshot().settings.window,
    },
    () => {},
  );
  let policy: RequestPolicy = {
    quitting: false,
    updateApplying: false,
    adoptionPending: false,
    administrationPending: false,
  };
  const updateState = emptyUpdateState();
  const context: IpcContext = {
    ipc,
    clipboard: {
      writeText: (value) => {
        copied.push(value);
      },
    },
    guard,
    controller,
    updates: {
      snapshot: updateState,
      check: async () => updateState,
      install: async () => updateState,
      setChannel: async () => updateState,
    },
    preflight: new AdministrationPreflight(),
    documentScope,
    policy: () => policy,
    theme: () => ({
      dark: false,
      highContrast: false,
      reducedMotion: false,
      accent: '#000000',
      material: 'solid',
    }),
    requestAdministration: async () => confirmed,
    retryBackend: async () => {},
    selectionChanged: () => {},
    documentReady: () => {},
  };
  const connect = async () => {
    const lease = (await ipc.invoke('manager:connect', event, undefined)) as ReturnType<
      DocumentGuard['connect']
    >;
    return (name: string, body: unknown = {}) =>
      ipc.invoke(`manager:${name}`, event, { token: lease.token, body });
  };
  return {
    ipc,
    guard,
    event,
    frame,
    context,
    documentScope,
    copied,
    connect,
    setPolicy: (value: Partial<RequestPolicy>) => {
      policy = { ...policy, ...value };
    },
  };
}

test('the registered administration request keeps zero arguments and the native UI consent path', async () => {
  const f = fixture();
  const argumentCounts: number[] = [];
  let confirms = 0,
    dispatches = 0;
  const administration = new ElevationCoordinator({
    eligible: () => true,
    preflight: async () => true,
    confirm: async () => {
      confirms++;
      return false;
    },
    quiesce: () => {},
    drain: async () => {},
    persistPlacement: async () => {},
    request: async (_action, payload): Promise<ElevationStatus> => {
      dispatches++;
      return {
        operationId: (payload as { operationId: string }).operationId,
        revision: '1',
        state: 'Denied',
        reason: 'Cancelled',
        receiptId: null,
        canResumeNormal: true,
      };
    },
    quitAfterAcknowledgement: async () => {},
  });
  f.context.requestAdministration = async function (explicitCli = false) {
    argumentCounts.push(arguments.length);
    return administration.request(explicitCli);
  };
  registerHandlers(f.context);
  const invoke = await f.connect();
  const result = (await invoke('requestAdministration')) as OperationResult;
  assert.deepEqual(argumentCounts, [0]);
  assert.equal(confirms, 1);
  assert.equal(dispatches, 0);
  assert.equal(result.outcome, 'CancelledBeforeDispatch');
});

test('registered handlers enforce every transition policy before dispatching a mutation', async () => {
  for (const phase of [
    'quitting',
    'updateApplying',
    'adoptionPending',
    'administrationPending',
  ] as const) {
    const f = fixture();
    f.setPolicy({ [phase]: true });
    registerHandlers(f.context);
    const invoke = await f.connect();
    await assert.rejects(invoke('copyText', { text: 'blocked mutation' }));
    assert.deepEqual(f.copied, []);
  }
  for (const phase of ['quitting', 'updateApplying', 'adoptionPending'] as const) {
    const f = fixture();
    let requests = 0;
    f.context.requestAdministration = async () => {
      requests++;
      return confirmed;
    };
    f.setPolicy({ [phase]: true });
    registerHandlers(f.context);
    const invoke = await f.connect();
    await assert.rejects(invoke('requestAdministration'));
    assert.equal(requests, 0);
  }
});

test('document authentication and payload validation precede policy and native administration', async () => {
  const f = fixture();
  let policies = 0,
    requests = 0;
  f.context.policy = () => {
    policies++;
    return {
      quitting: false,
      updateApplying: false,
      adoptionPending: false,
      administrationPending: false,
    };
  };
  f.context.requestAdministration = async () => {
    requests++;
    return confirmed;
  };
  registerHandlers(f.context);
  const invoke = await f.connect();
  await assert.rejects(
    f.ipc.invoke('manager:requestAdministration', f.event, { token: 'forged', body: {} }),
    /Stale IPC document/,
  );
  await assert.rejects(invoke('requestAdministration', { explicitCli: true }), /Invalid/);
  assert.equal(policies, 0);
  assert.equal(requests, 0);
});

test('an obsolete document invalidates async scope and cannot receive a completed handler response', async () => {
  const f = fixture();
  let release!: () => void;
  const pending = new Promise<void>((resolve) => {
    release = resolve;
  });
  const observed: (boolean | undefined)[] = [];
  f.context.requestAdministration = async () => {
    observed.push(f.documentScope.getStore()?.());
    await pending;
    observed.push(f.documentScope.getStore()?.());
    return confirmed;
  };
  registerHandlers(f.context);
  const invoke = await f.connect();
  const request = invoke('requestAdministration');
  assert.deepEqual(observed, [true]);
  f.guard.invalidate();
  release();
  await assert.rejects(request, /document/i);
  assert.deepEqual(observed, [true, false]);
  assert.equal(f.documentScope.getStore(), undefined);
});

test('fire-and-forget registration authenticates readiness before changing application state', async () => {
  const f = fixture();
  let ready = 0;
  f.context.documentReady = () => {
    ready++;
  };
  registerHandlers(f.context);
  const lease = (await f.ipc.invoke('manager:connect', f.event, undefined)) as ReturnType<
    DocumentGuard['connect']
  >;
  const notify = f.ipc.notifications.get('manager:documentReady');
  assert.ok(notify);
  const invalidFrame = { ...f.event, senderFrame: { ...f.frame } } as unknown as IpcMainEvent;
  notify(invalidFrame, { token: lease.token, body: {} });
  assert.equal(ready, 0);
  notify(f.event as unknown as IpcMainEvent, { token: lease.token, body: {} });
  assert.equal(ready, 1);
  f.guard.invalidate();
  notify(f.event as unknown as IpcMainEvent, { token: lease.token, body: {} });
  assert.equal(ready, 1);
});

test('a validated copy request reaches only the injected clipboard port', async () => {
  const f = fixture();
  registerHandlers(f.context);
  const invoke = await f.connect();
  await invoke('copyText', { text: 'owned fixture text' });
  assert.deepEqual(f.copied, ['owned fixture text']);
});

test('channel requests authenticate the document and accept only the two explicit channels', async () => {
  const f = fixture();
  const requested: UpdateChannel[] = [];
  f.context.updates.setChannel = async (channel) => {
    requested.push(channel);
    return { ...emptyUpdateState(), channel };
  };
  registerHandlers(f.context);
  const invoke = await f.connect();
  await assert.rejects(
    f.ipc.invoke('manager:setUpdateChannel', f.event, {
      token: 'forged',
      body: { channel: 'test' },
    }),
    /Stale IPC document/,
  );
  for (const body of [
    {},
    { channel: 'beta' },
    { channel: null },
    { channel: 'test', repository: 'another/repository' },
  ]) {
    await assert.rejects(invoke('setUpdateChannel', body), /Invalid/);
  }
  assert.deepEqual(requested, []);
  await invoke('setUpdateChannel', { channel: 'test' });
  await invoke('setUpdateChannel', { channel: 'stable' });
  assert.deepEqual(requested, ['test', 'stable']);
});

test('channel changes cannot bypass a pending application transition', async () => {
  for (const phase of [
    'quitting',
    'updateApplying',
    'adoptionPending',
    'administrationPending',
  ] as const) {
    const f = fixture();
    let requests = 0;
    f.context.updates.setChannel = async () => {
      requests++;
      return emptyUpdateState();
    };
    f.setPolicy({ [phase]: true });
    registerHandlers(f.context);
    const invoke = await f.connect();
    await assert.rejects(invoke('setUpdateChannel', { channel: 'test' }));
    assert.equal(requests, 0);
  }
});

test('the public updater does not register a GitHub credential endpoint', async () => {
  const f = fixture();
  registerHandlers(f.context);
  const invoke = await f.connect();
  await assert.rejects(
    invoke('setUpdateToken', { token: 'obsolete-fixture-value' }),
    /Unregistered handler/,
  );
  assert.equal(f.ipc.handlers.has('manager:setUpdateToken'), false);
});
