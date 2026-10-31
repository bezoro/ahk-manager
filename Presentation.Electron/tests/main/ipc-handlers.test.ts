import test from 'node:test';
import assert from 'node:assert/strict';
import { AsyncLocalStorage } from 'node:async_hooks';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { CommandLog } from '../../src/main/logging/command-log.js';
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

test('command logs cover authenticated success, validation refusal, native failure and cancellation without request contents', async (t) => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'ahk-command-ipc-'));
  const log = new CommandLog(directory);
  t.after(() => {
    log.close();
    fs.rmSync(directory, { recursive: true, force: true });
  });
  const f = fixture();
  f.context.commandLog = log;
  f.context.controller.setSnapshot(snapshot());
  f.context.controller.setConnection('ready');
  let opened = 0;
  f.context.openLogs = async () => {
    opened++;
    throw Object.assign(new Error('Private native error text must not reach the log'), {
      code: 'EACCES',
    });
  };
  registerHandlers(f.context);
  const invoke = await f.connect();
  const clipboardSecret = 'clipboard-secret-that-must-not-reach-disk';
  await invoke('copyText', { text: clipboardSecret });
  await assert.rejects(invoke('openLogs', { path: clipboardSecret }), /Invalid/);
  await assert.rejects(invoke('openLogs'), /Private native error/);
  assert.equal(opened, 1);
  const cancellation = await invoke('previewImport');
  assert.equal(cancellation, null);
  f.guard.invalidate();
  await assert.rejects(invoke('copyText', { text: 'unauthenticated-secret' }), /Stale/);
  assert.deepEqual(f.copied, [clipboardSecret]);
  log.close();
  const text = fs
    .readdirSync(log.directory)
    .map((file) => fs.readFileSync(path.join(log.directory, file), 'utf8'))
    .join('');
  const records = text
    .trim()
    .split('\n')
    .map((line) => JSON.parse(line));
  assert.ok(
    records.some((record) => record.command === 'copyText' && record.phase === 'completed'),
  );
  assert.ok(records.some((record) => record.command === 'openLogs' && record.phase === 'error'));
  assert.ok(records.some((record) => record.errorCode === 'EACCES'));
  assert.ok(
    records.some(
      (record) => record.command === 'dispatch.previewImport' && record.result === 'cancelled',
    ),
  );
  assert.doesNotMatch(text, /clipboard-secret|unauthenticated-secret|Private native error/);
  for (const start of records.filter((record) => record.phase === 'started'))
    assert.equal(
      records.filter((record) => record.requestId === start.requestId && record.phase !== 'started')
        .length,
      1,
    );
});

test('opening logs uses a fixed host action during recovery, with no renderer path or shutdown bypass', async () => {
  const f = fixture();
  let opened = 0;
  f.context.openLogs = async () => {
    opened++;
  };
  registerHandlers(f.context);
  const invoke = await f.connect();
  for (const phase of ['updateApplying', 'adoptionPending', 'administrationPending'] as const) {
    f.setPolicy({ [phase]: true });
    await invoke('openLogs');
    f.setPolicy({ [phase]: false });
  }
  assert.equal(opened, 3);
  await assert.rejects(invoke('openLogs', { path: 'C:\\untrusted' }), /Invalid/);
  f.setPolicy({ quitting: true });
  await assert.rejects(invoke('openLogs'), /closing/);
  assert.equal(opened, 3);
});

test('diagnostic copying authenticates and validates a fixed mode before writing the native clipboard', async () => {
  const f = fixture();
  const modes: string[] = [];
  f.context.diagnosticReport = (mode) => {
    modes.push(mode);
    return 'Safe report: ' + mode;
  };
  registerHandlers(f.context);
  const invoke = await f.connect();
  for (const mode of ['recent', 'errors']) await invoke('copyDiagnostics', { mode });
  assert.deepEqual(f.copied, ['Safe report: recent', 'Safe report: errors']);
  for (const body of [
    {},
    { mode: 'all' },
    { mode: 'errors', path: 'C:\\secret' },
    { mode: 'recent', text: 'private' },
  ])
    await assert.rejects(invoke('copyDiagnostics', body), /Invalid/);
  assert.deepEqual(modes, ['recent', 'errors']);
  f.guard.invalidate();
  await assert.rejects(invoke('copyDiagnostics', { mode: 'recent' }), /Stale/);
  assert.equal(f.copied.length, 2);
});

test('diagnostic copying works during recovery but respects shutdown and document ownership', async () => {
  const f = fixture();
  f.context.diagnosticReport = () => 'Safe report';
  registerHandlers(f.context);
  const invoke = await f.connect();
  for (const phase of ['updateApplying', 'adoptionPending', 'administrationPending'] as const) {
    f.setPolicy({ [phase]: true });
    await invoke('copyDiagnostics', { mode: 'errors' });
    f.setPolicy({ [phase]: false });
  }
  assert.equal(f.copied.length, 3);
  f.setPolicy({ quitting: true });
  await assert.rejects(invoke('copyDiagnostics', { mode: 'recent' }), /closing/);
  f.setPolicy({ quitting: false });
  f.context.diagnosticReport = () => {
    f.guard.invalidate();
    return 'Must not be copied';
  };
  await assert.rejects(invoke('copyDiagnostics', { mode: 'recent' }), /document changed/i);
  assert.equal(f.copied.length, 3);
});

test('report preparation and clipboard failures are surfaced without leaking, truncating or repeating a copy', async () => {
  const f = fixture();
  registerHandlers(f.context);
  const invoke = await f.connect();
  await assert.rejects(invoke('copyDiagnostics', { mode: 'recent' }), /unavailable/);
  f.context.diagnosticReport = () => 'ç'.repeat(32769);
  await assert.rejects(invoke('copyDiagnostics', { mode: 'recent' }), /prepared/);
  assert.equal(f.copied.length, 0);
  f.context.diagnosticReport = () => 'Safe report';
  let attempts = 0;
  f.context.clipboard.writeText = () => {
    attempts++;
    throw new Error('Clipboard is busy');
  };
  await assert.rejects(invoke('copyDiagnostics', { mode: 'errors' }), /Clipboard is busy/);
  assert.equal(attempts, 1);
});

test('archive export has no renderer paths, authenticates, and remains available during recovery', async () => {
  const f = fixture();
  let exports = 0;
  f.context.exportDiagnostics = async () => {
    exports++;
    return { complete: false, issueCount: 2 };
  };
  registerHandlers(f.context);
  const invoke = await f.connect();
  for (const phase of ['updateApplying', 'adoptionPending', 'administrationPending'] as const) {
    f.setPolicy({ [phase]: true });
    assert.deepEqual(await invoke('exportDiagnostics'), { complete: false, issueCount: 2 });
    f.setPolicy({ [phase]: false });
  }
  await assert.rejects(invoke('exportDiagnostics', { path: 'C:\\untrusted.zip' }), /Invalid/);
  f.setPolicy({ quitting: true });
  await assert.rejects(invoke('exportDiagnostics'), /closing/);
  f.setPolicy({ quitting: false });
  f.guard.invalidate();
  await assert.rejects(invoke('exportDiagnostics'), /Stale/);
  assert.equal(exports, 3);
  assert.deepEqual(f.copied, []);
});

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

test('data location actions accept no renderer paths and obey authenticated transition guards', async () => {
  const f = fixture();
  let choices = 0;
  const state = {
    mode: 'portable' as const,
    currentDataDirectory: 'C:\\Manager\\Data',
    defaultDataDirectory: 'C:\\Manager\\Data',
    pendingDataDirectory: null,
    canChange: true,
    error: null,
  };
  f.context.storage = {
    get: () => state,
    choose: async () => {
      choices++;
      return null;
    },
    open: async () => {},
    restoreDefault: async () => ({ state, restartRequired: false }),
    restart: async () => {},
    cancel: async () => state,
  };
  registerHandlers(f.context);
  const invoke = await f.connect();
  await assert.rejects(invoke('storage:choose', { path: 'C:\\Another user' }), /Invalid/i);
  assert.equal(choices, 0);
  for (const transition of [
    'quitting',
    'updateApplying',
    'adoptionPending',
    'administrationPending',
  ] as const) {
    f.setPolicy({ [transition]: true });
    await assert.rejects(invoke('storage:choose'));
    assert.deepEqual(await invoke('storage:get'), state);
    f.setPolicy({ [transition]: false });
  }
  await invoke('storage:choose');
  assert.equal(choices, 1);
  f.guard.invalidate();
  await assert.rejects(invoke('storage:choose'), /Stale/i);
  assert.equal(choices, 1);
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
