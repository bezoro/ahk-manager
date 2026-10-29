import test from 'node:test';
import assert from 'node:assert/strict';
import { assertRequestAllowed, type RequestPolicy } from '../../src/main/ipc/request-policy.js';

const idle: RequestPolicy = {
  quitting: false,
  updateApplying: false,
  adoptionPending: false,
  administrationPending: false,
};

test('host transitions reject mutations while retaining state and theme reads', () => {
  for (const phase of [
    'quitting',
    'updateApplying',
    'adoptionPending',
    'administrationPending',
  ] as const) {
    const state = { ...idle, [phase]: true };
    for (const mutation of [
      'operate',
      'saveSettings',
      'retryBackend',
      'installUpdate',
      'removeScript',
    ]) {
      assert.throws(() => assertRequestAllowed(mutation, state), `${phase} must block ${mutation}`);
    }
    assert.doesNotThrow(() => assertRequestAllowed('getState', state));
    assert.doesNotThrow(() => assertRequestAllowed('getTheme', state));
  }
});

test('administration and adoption retain their distinct recovery routes', () => {
  assert.doesNotThrow(() =>
    assertRequestAllowed('getActivity', { ...idle, adoptionPending: true }),
  );
  assert.doesNotThrow(() =>
    assertRequestAllowed('getActivity', { ...idle, administrationPending: true }),
  );
  assert.doesNotThrow(() =>
    assertRequestAllowed('requestAdministration', { ...idle, administrationPending: true }),
  );
  assert.throws(() =>
    assertRequestAllowed('requestAdministration', { ...idle, adoptionPending: true }),
  );
  assert.throws(() => assertRequestAllowed('getActivity', { ...idle, quitting: true }));
});

test('overlapping transitions must satisfy every guard, including update status reads', () => {
  assert.doesNotThrow(() =>
    assertRequestAllowed('getUpdateState', { ...idle, updateApplying: true }),
  );
  assert.throws(() =>
    assertRequestAllowed('getUpdateState', {
      ...idle,
      updateApplying: true,
      administrationPending: true,
    }),
  );
  assert.throws(() =>
    assertRequestAllowed('requestAdministration', {
      ...idle,
      administrationPending: true,
      quitting: true,
    }),
  );
  assert.doesNotThrow(() => assertRequestAllowed('operate', idle));
});
