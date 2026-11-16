import assert from 'node:assert/strict';
import test from 'node:test';
import { FixtureResponseStore } from './fixture-response-store.js';

test('failed-scenario cleanup settles every pending response and leaves the next case usable', async () => {
  const store = new FixtureResponseStore();
  store.set('openLogs', { pending: true });
  const first = store.wait('openLogs');
  const duplicate = store.wait('openLogs');
  const other = store.wait('copyDiagnostics');
  const completed = Promise.allSettled([first, duplicate, other]);
  assert.throws(() => store.resolve('openLogs', {}), /Multiple pending/);
  store.endScenario();
  const results = await completed;
  assert.ok(results.every((result) => result.status === 'rejected'));
  store.reset();
  assert.equal(store.get('openLogs'), undefined);
  const next = store.wait('openLogs');
  store.resolve('openLogs', { value: 'next scenario' });
  assert.equal(await next, 'next scenario');
});

test('ordinary fixture reset still detects unresolved requests rather than hiding missing assertions', async () => {
  const store = new FixtureResponseStore();
  store.set('installUpdate', { pending: true });
  const pending = store.wait('installUpdate');
  assert.throws(() => store.reset(), /Unresolved fixture/);
  store.resolve('installUpdate', { value: { phase: 'idle' } });
  assert.deepEqual(await pending, { phase: 'idle' });
  store.reset();
  assert.equal(store.get('installUpdate'), undefined);
  assert.throws(() => store.resolve('installUpdate', {}), /No pending fixture/);
});
