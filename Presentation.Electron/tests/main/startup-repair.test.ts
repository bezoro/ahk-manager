import test from 'node:test';
import assert from 'node:assert/strict';
import { ManagerController, type NativeBoundary } from '../../src/main/controller.js';
import type { OperationResult, StartupRepairPreview } from '../../src/shared/contracts.js';
import {
  startupRepairFingerprint,
  startupRepairPreview,
} from '../../src/shared/startup-repair-validation.js';
import { snapshot } from './fixtures.js';

const fingerprint = 'c'.repeat(64);
const review: StartupRepairPreview = {
  canRepair: true,
  previousExecutable: 'V:\\Previous Manager\\Bezoro.AhkManager.exe',
  currentExecutable: 'C:\\Portable Manager\\Bezoro.AhkManager.exe',
  fingerprint,
  reason: 'None',
  detail: 'Review the previous startup entry before removing it.',
};
const confirmed: OperationResult = {
  outcome: 'Confirmed',
  code: 'None',
  detail: null,
  correlationId: null,
};

function setup() {
  const requests: { action: string; payload: unknown }[] = [];
  let confirmations = 0;
  const native: NativeBoundary = {
    confirm: async () => {
      confirmations++;
      return false;
    },
    files: async () => [],
    alive: () => true,
    placement: () => snapshot().settings.window,
  };
  const authority = {
    request: async <T>(action: string, payload: unknown): Promise<T> => {
      requests.push({ action, payload });
      return (action === 'previewStartupRepair' ? structuredClone(review) : confirmed) as T;
    },
  };
  const controller = new ManagerController(authority, native, () => {});
  controller.setSnapshot(snapshot());
  controller.setConnection('ready');
  return { controller, authority, native, requests, confirmations: () => confirmations };
}

test('startup repair previews validate finite reasons, bounded locations and exact snapshot tokens', () => {
  assert.equal(startupRepairPreview(review), true);
  assert.equal(
    startupRepairPreview({
      canRepair: false,
      previousExecutable: null,
      currentExecutable: null,
      fingerprint: null,
      reason: 'StartupEntryConflict',
      detail: null,
    }),
    true,
  );
  for (const value of [
    null,
    1,
    '',
    'a'.repeat(63),
    'a'.repeat(65),
    'A'.repeat(64),
    'a'.repeat(64) + '\n',
    'a'.repeat(64) + '\r\n',
  ])
    assert.equal(startupRepairFingerprint(value), false);
  for (const invalid of [
    { ...review, fingerprint: null },
    { ...review, canRepair: false },
    { ...review, previousExecutable: '' },
    { ...review, currentExecutable: null },
    { ...review, currentExecutable: 'bad\0path' },
    { ...review, previousExecutable: 'x'.repeat(32769) },
    { ...review, detail: 'x'.repeat(4097) },
    { ...review, reason: 'unrecognized-reason' },
    { ...review, rawCommand: 'unapproved command' },
  ])
    assert.equal(startupRepairPreview(invalid), false);
});

test('the explicit reviewed action dispatches one token and consent without another native dialog', async () => {
  const f = setup();
  assert.deepEqual(await f.controller.previewStartupRepair(), review);
  assert.equal(f.requests.length, 1);
  assert.deepEqual(await f.controller.repairStartup(fingerprint), confirmed);
  assert.deepEqual(f.requests, [
    { action: 'previewStartupRepair', payload: {} },
    { action: 'repairStartup', payload: { fingerprint, confirmed: true } },
  ]);
  assert.equal(f.confirmations(), 0);
  assert.equal((await f.controller.repairStartup(fingerprint)).code, 'StaleSelection');
  assert.equal(f.requests.length, 2);
});

test('missing, unavailable and changed startup reviews never authorize a repair', async () => {
  const missing = setup();
  assert.equal((await missing.controller.repairStartup(fingerprint)).code, 'StaleSelection');
  assert.deepEqual(missing.requests, []);

  const wrong = setup();
  await wrong.controller.previewStartupRepair();
  assert.equal((await wrong.controller.repairStartup('d'.repeat(64))).code, 'StaleSelection');
  assert.equal(wrong.requests.length, 1);

  const unavailable = setup();
  unavailable.authority.request = async <T>(): Promise<T> =>
    ({
      ...review,
      canRepair: false,
      fingerprint: null,
      reason: 'StartupEntryConflict',
    }) as T;
  await unavailable.controller.previewStartupRepair();
  assert.equal((await unavailable.controller.repairStartup(fingerprint)).code, 'StaleSelection');
});

test('startup review expires and cannot survive saved startup mode, authority or connection changes', async (t) => {
  let clock = 1000;
  t.mock.method(Date, 'now', () => clock);
  for (const change of [
    'expired',
    'launchManagerAtLogin',
    'launchManagerAsAdministrator',
    'authority',
    'connection',
  ]) {
    const f = setup();
    await f.controller.previewStartupRepair();
    if (change === 'expired') clock += 300001;
    if (change === 'launchManagerAtLogin' || change === 'launchManagerAsAdministrator') {
      const state = snapshot();
      state.settings[change] = !state.settings[change];
      f.controller.setSnapshot(state);
    }
    if (change === 'authority') f.controller.setAuthority(f.authority);
    if (change === 'connection') {
      f.controller.setConnection('failed');
      f.controller.setConnection('ready');
    }
    assert.equal((await f.controller.repairStartup(fingerprint)).code, 'StaleSelection');
    assert.equal(f.requests.filter((request) => request.action === 'repairStartup').length, 0);
  }
});

test('a late preview cannot authorize a closed document or a changed saved startup mode', async () => {
  for (const change of ['closed', 'launchManagerAtLogin', 'launchManagerAsAdministrator']) {
    const f = setup();
    let finish!: (value: StartupRepairPreview) => void;
    const pending = new Promise<StartupRepairPreview>((resolve) => (finish = resolve));
    f.authority.request = async <T>(): Promise<T> => (await pending) as T;
    const preview = f.controller.previewStartupRepair();
    if (change === 'closed') f.native.alive = () => false;
    else if (change === 'launchManagerAtLogin' || change === 'launchManagerAsAdministrator') {
      const state = snapshot();
      state.settings[change] = !state.settings[change];
      f.controller.setSnapshot(state);
    }
    finish(review);
    await assert.rejects(preview, /document/i);
    f.native.alive = () => true;
    assert.equal((await f.controller.repairStartup(fingerprint)).code, 'StaleSelection');
  }
});

test('unrelated observation and preference revisions preserve a reviewed startup entry', async () => {
  const f = setup();
  await f.controller.previewStartupRepair();
  const refreshed = snapshot();
  refreshed.revision = '2';
  refreshed.observedAt = '2026-11-03T12:00:00Z';
  refreshed.scripts[0]!.friendlyName = 'Refreshed script observation';
  refreshed.settings.editorArguments = ['updated unrelated preference'];
  f.controller.setSnapshot(refreshed);
  assert.deepEqual(await f.controller.repairStartup(fingerprint), confirmed);
  assert.equal(f.requests.filter((request) => request.action === 'repairStartup').length, 1);
});

test('an observation revision during readonly startup inspection does not invalidate its mode', async () => {
  const f = setup();
  let finish!: (value: StartupRepairPreview) => void;
  const pending = new Promise<StartupRepairPreview>((resolve) => (finish = resolve));
  f.authority.request = async <T>(action: string, payload: unknown): Promise<T> => {
    f.requests.push({ action, payload });
    return (action === 'previewStartupRepair' ? await pending : confirmed) as T;
  };
  const preview = f.controller.previewStartupRepair();
  f.controller.setSnapshot({ ...snapshot(), revision: '2' });
  finish(review);
  assert.deepEqual(await preview, review);
  assert.deepEqual(await f.controller.repairStartup(fingerprint), confirmed);
});

test('a failed preview invalidates an older successful review', async () => {
  const f = setup();
  await f.controller.previewStartupRepair();
  f.authority.request = async () => {
    throw new Error('The startup inspection is unavailable.');
  };
  await assert.rejects(f.controller.previewStartupRepair(), /inspection is unavailable/);
  assert.equal((await f.controller.repairStartup(fingerprint)).code, 'StaleSelection');
});

test('pending startup reviews cannot survive authority or connection replacement with the same revision', async () => {
  for (const change of ['authority', 'connection']) {
    const f = setup();
    let finish!: (value: StartupRepairPreview) => void;
    const pending = new Promise<StartupRepairPreview>((resolve) => (finish = resolve));
    f.authority.request = async <T>(): Promise<T> => (await pending) as T;
    const preview = f.controller.previewStartupRepair();
    if (change === 'authority') f.controller.setAuthority(f.authority);
    else {
      f.controller.setConnection('failed');
      f.controller.setConnection('ready');
    }
    finish(review);
    await assert.rejects(preview, /document/i);
    assert.equal((await f.controller.repairStartup(fingerprint)).code, 'StaleSelection');
  }
});

test('an older preview response cannot replace the newest reviewed startup entry', async () => {
  const f = setup();
  let finishOld!: (value: StartupRepairPreview) => void;
  const oldReply = new Promise<StartupRepairPreview>((resolve) => (finishOld = resolve));
  let previews = 0;
  const current = { ...review, fingerprint: 'd'.repeat(64) };
  f.authority.request = async <T>(action: string, payload: unknown): Promise<T> => {
    f.requests.push({ action, payload });
    if (action === 'previewStartupRepair') {
      previews++;
      return (previews === 1 ? await oldReply : current) as T;
    }
    return confirmed as T;
  };
  const old = f.controller.previewStartupRepair();
  assert.deepEqual(await f.controller.previewStartupRepair(), current);
  finishOld(review);
  await assert.rejects(old, /document/i);
  assert.deepEqual(await f.controller.repairStartup(current.fingerprint), confirmed);
  assert.deepEqual(f.requests.at(-1), {
    action: 'repairStartup',
    payload: { fingerprint: current.fingerprint, confirmed: true },
  });
});

test('queued startup repair accepts observation refresh but rejects mode and authority changes', async () => {
  for (const change of ['observation', 'mode', 'authority', 'connection']) {
    const f = setup();
    await f.controller.previewStartupRepair();
    let finish!: () => void;
    const pending = new Promise<void>((resolve) => (finish = resolve));
    f.authority.request = async <T>(action: string, payload: unknown): Promise<T> => {
      f.requests.push({ action, payload });
      if (action === 'runLogin') {
        await pending;
        const refreshed = snapshot();
        refreshed.revision = '2';
        if (change === 'mode')
          refreshed.settings.launchManagerAsAdministrator =
            !refreshed.settings.launchManagerAsAdministrator;
        if (change === 'authority') f.controller.setAuthority(f.authority);
        if (change === 'connection') {
          f.controller.setConnection('failed');
          f.controller.setConnection('ready');
        }
        f.controller.setSnapshot(refreshed);
      }
      return confirmed as T;
    };
    const previous = f.controller.runLogin();
    for (let attempt = 0; attempt < 20 && f.requests.length < 2; attempt++)
      await new Promise((resolve) => setImmediate(resolve));
    const repairing = f.controller.repairStartup(fingerprint);
    finish();
    await previous;
    assert.equal((await repairing).code, change === 'observation' ? 'None' : 'StaleSelection');
    assert.equal(
      f.requests.filter((request) => request.action === 'repairStartup').length,
      change === 'observation' ? 1 : 0,
    );
  }
});
