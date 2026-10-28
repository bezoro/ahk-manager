import test from 'node:test';
import assert from 'node:assert/strict';
import { isEarlierUpdateVersion } from '../../src/shared/updates.js';

test('rollback wording applies only to releases older than the installed version', () => {
  assert.equal(isEarlierUpdateVersion('1.2.0', '1.2.1'), true);
  assert.equal(isEarlierUpdateVersion('1.2.1', '1.2.1'), false);
  assert.equal(isEarlierUpdateVersion('1.2.2', '1.2.1'), false);
  assert.equal(isEarlierUpdateVersion('1.10.0', '1.9.0'), false);
  assert.equal(isEarlierUpdateVersion('1.9.0', '1.10.0'), true);
  assert.equal(isEarlierUpdateVersion('2.0.0', '1.99.99'), false);
});

test('unknown version syntax uses neutral installation wording', () => {
  for (const version of ['', 'latest', '1.2', '1.2.0-beta', '1.2.0.0', '01.2.0', '9007199254740993.0.0']) {
    assert.equal(isEarlierUpdateVersion(version, '1.2.1'), false);
    assert.equal(isEarlierUpdateVersion('1.2.0', version), false);
  }
});
