import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
const { validateRelative } = require('../../scripts/lib/portable-paths.cjs') as {
  validateRelative(value: string): string;
};
const { writePortablePublisherPolicy } =
  require('../../scripts/lib/portable-publisher-policy.cjs') as {
    writePortablePublisherPolicy(root: string, publisher: string | null): void;
  };

test('portable paths reject escapes, devices and ambiguous Windows file names', () => {
  for (const value of [
    '../file',
    'a/../../b',
    'C:/file',
    '/file',
    'a//b',
    'a/$value',
    'a/"file',
    'NUL',
    'a/CON.txt',
    'a/file.',
  ])
    assert.throws(() => validateRelative(value), value);
  assert.equal(
    validateRelative('backend\\update\\AhkManager.UpdateHost.exe'),
    'backend/update/AhkManager.UpdateHost.exe',
  );
});

test('directory packaging writes the exact portable publisher policy and preserves a conflicting file', () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'ahk-portable-policy-'));
  try {
    writePortablePublisherPolicy(root, null);
    const file = path.join(root, 'resources/app-update.yml');
    const original = fs.readFileSync(file);
    assert.equal(Object.hasOwn(JSON.parse(original.toString('utf8')), 'publisherName'), false);
    writePortablePublisherPolicy(root, null);
    assert.deepEqual(fs.readFileSync(file), original);
    assert.throws(() => writePortablePublisherPolicy(root, 'Different Publisher'));
    assert.deepEqual(fs.readFileSync(file), original);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});
