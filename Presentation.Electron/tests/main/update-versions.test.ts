import test from 'node:test';
import assert from 'node:assert/strict';
import {
  compareUpdateVersions,
  parseUpdateVersion,
  validUpdateVersion,
  versionAllowedInChannel,
} from '../../src/shared/update-versions.js';
import { isEarlierUpdateVersion } from '../../src/shared/updates.js';

test('published test versions use numeric SemVer ordering and remain lower than their stable base', () => {
  const ordered = [
    '1.2.16',
    '1.3.0-test.2.9.1',
    '1.3.0-test.2.10.1',
    '1.3.0-test.2.10.2',
    '1.3.0-test.10.1.1',
    '1.3.0',
    '1.3.1-test.1.1.1',
  ];
  for (let index = 0; index < ordered.length - 1; index++) {
    assert.equal(compareUpdateVersions(ordered[index]!, ordered[index + 1]!), -1);
    assert.equal(compareUpdateVersions(ordered[index + 1]!, ordered[index]!), 1);
    assert.equal(isEarlierUpdateVersion(ordered[index]!, ordered[index + 1]!), true);
  }
  assert.equal(compareUpdateVersions('1.3.0-test.2.10.1', '1.3.0-test.2.10.1'), 0);
  assert.equal(versionAllowedInChannel('1.3.0-test.2.10.1', 'stable'), false);
  assert.equal(versionAllowedInChannel('1.3.0-test.2.10.1', 'test'), true);
  assert.equal(versionAllowedInChannel('1.3.0', 'test'), true);
});

test('update identities reject unsupported prereleases and noncanonical or unbounded numeric identifiers', () => {
  for (const value of [
    '1.3.0-beta.1',
    '1.3.0-test.0.1.1',
    '1.3.0-test.1.0.1',
    '1.3.0-test.1.1.0',
    '1.3.0-test.01.1.1',
    '1.3.0-test.1.01.1',
    '1.3.0-test.1.1.01',
    '1.3.0-test.1.1',
    '1.3.0-test.1.1.1.extra',
    '1.3.0-test.2147483648.1.1',
    '2147483648.0.0',
    '1.3.0-test.1.1.1+build',
    '../1.3.0-test.1.1.1',
    '1.3.0-test.1.1.1\0',
    '1.3.0-test.1.1.1\n',
    '1.3.0\n',
  ]) {
    assert.equal(parseUpdateVersion(value), null, value);
    assert.equal(validUpdateVersion(value), false, value);
    assert.equal(isEarlierUpdateVersion(value, '1.3.0'), false, value);
  }
  assert.ok(parseUpdateVersion('2147483647.2147483647.2147483647-test.2147483647.1.1'));
  assert.equal(
    parseUpdateVersion('2147483647.2147483647.2147483647-test.2147483647.2147483647.2147483647'),
    null,
  );
  assert.equal(validUpdateVersion('1.1.999-test.1.1.1'), false);
});
