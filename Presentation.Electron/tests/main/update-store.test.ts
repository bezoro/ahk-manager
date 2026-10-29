import test, { type TestContext } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { UpdateStore, type CachedInstaller } from '../../src/main/updates/update-store.js';

const sha = (bytes: Buffer | string): string => createHash('sha256').update(bytes).digest('hex');

async function fixture(t: TestContext) {
  const parent = process.env.AHK_DIAGNOSTICS_TEST_ROOT;
  assert.ok(
    parent && path.isAbsolute(parent),
    'The test dispatcher must provide its isolated staging root.',
  );
  const root = await fs.mkdtemp(path.join(parent, 'update-store-'));
  t.after(async () => {
    await fs.rm(root, { recursive: true, force: true });
  });
  const store = new UpdateStore(path.join(root, 'cache'));
  const source = path.join(root, 'download with espaço.exe');
  const body = Buffer.from('Owned installer fixture bytes. Never executed.');
  await fs.writeFile(source, body);
  return { root, store, source, body };
}

async function linkOrSkip(t: TestContext, target: string, link: string): Promise<boolean> {
  try {
    await fs.symlink(target, link, process.platform === 'win32' ? 'junction' : 'dir');
    return true;
  } catch (error) {
    if (['EPERM', 'EACCES', 'ENOTSUP'].includes((error as NodeJS.ErrnoException).code ?? '')) {
      t.skip('The runner cannot create the owned redirect fixture.');
      return false;
    }
    throw error;
  }
}

test('real installer bytes and receipt hashes are verified before becoming a cached version', async (t) => {
  const { store, source, body } = await fixture(t);
  const value = await store.cache('1.2.4', source, sha(body), body.length);
  assert.deepEqual(await fs.readFile(value.installerPath), body);
  await store.verify(value);
  const receipt = JSON.parse(
    await fs.readFile(path.join(path.dirname(value.installerPath), 'receipt.json'), 'utf8'),
  );
  assert.deepEqual(receipt, {
    schemaVersion: 1,
    updateProtocol: 1,
    version: '1.2.4',
    sha256: sha(body),
    bytes: body.length,
  });
  assert.deepEqual(await store.cached(), [value]);
  assert.deepEqual(await fs.readFile(source), body);
});

test('wrong hash, byte count, empty input and path-shaped version never produce an installer choice', async (t) => {
  const { store, source, body, root } = await fixture(t);
  await assert.rejects(store.cache('1.2.4', source, '0'.repeat(64), body.length), /integrity/);
  await assert.rejects(store.cache('1.2.4', source, sha(body), body.length + 1), /integrity/);
  for (const version of ['../1.2.4', '1.2', '01.2.4', 'v1.2.4', '1.2.4\0', '1.2.4-beta'])
    await assert.rejects(store.cache(version, source, sha(body), body.length), /invalid/);
  const empty = path.join(root, 'empty.exe');
  await fs.writeFile(empty, '');
  await assert.rejects(store.cache('1.2.4', empty, sha(''), 0), /invalid/);
  assert.deepEqual(await store.cached(), []);
  assert.deepEqual(await fs.readFile(source), body);
});

test('changed cached bytes are refused and a verified replacement preserves the corrupt evidence', async (t) => {
  const { store, source, body } = await fixture(t);
  const value = await store.cache('1.2.4', source, sha(body), body.length);
  const corrupt = Buffer.alloc(body.length, 0x58);
  await fs.writeFile(value.installerPath, corrupt);
  await assert.rejects(store.verify(value), /integrity/);
  const repaired = await store.cache('1.2.4', source, sha(body), body.length);
  await store.verify(repaired);
  const directory = path.dirname(value.installerPath);
  const preserved = (await fs.readdir(directory)).filter(
    (name) => name.startsWith('preserved-') && name.endsWith('.exe'),
  );
  assert.equal(preserved.length, 1);
  assert.deepEqual(await fs.readFile(path.join(directory, preserved[0]!)), corrupt);
  assert.deepEqual(await fs.readFile(repaired.installerPath), body);
});

test('valid repeated cache operation is idempotent and offline verification refuses an out-of-history path', async (t) => {
  const { store, source, body, root } = await fixture(t);
  const first = await store.cache('1.2.4', source, sha(body), body.length);
  const before = await fs.stat(first.installerPath);
  const second = await store.cache('1.2.4', source, sha(body), body.length);
  assert.deepEqual(second, first);
  assert.equal((await fs.stat(first.installerPath)).mtimeMs, before.mtimeMs);
  assert.deepEqual(
    (await fs.readdir(path.dirname(first.installerPath))).filter((name) =>
      name.startsWith('preserved-'),
    ),
    [],
  );
  const outside: CachedInstaller = { ...first, installerPath: path.join(root, 'outside.exe') };
  await fs.writeFile(outside.installerPath, body);
  await assert.rejects(store.verify(outside), /outside the version history/);
});

test('incomplete receipts and changed sizes are omitted from executable history', async (t) => {
  const { store, source, body } = await fixture(t);
  const value = await store.cache('1.2.4', source, sha(body), body.length);
  const receipt = path.join(path.dirname(value.installerPath), 'receipt.json');
  for (const document of [
    'not-json',
    '{}',
    JSON.stringify({ schemaVersion: 1, updateProtocol: 2, version: '1.2.4', sha256: sha(body) }),
    JSON.stringify({ schemaVersion: 1, updateProtocol: 1, version: '1.2.5', sha256: sha(body) }),
  ]) {
    await fs.writeFile(receipt, document);
    assert.deepEqual(await store.cached(), []);
  }
  await fs.writeFile(
    receipt,
    JSON.stringify({
      schemaVersion: 1,
      updateProtocol: 1,
      version: '1.2.4',
      sha256: sha(body),
      bytes: body.length + 1,
    }),
  );
  assert.deepEqual(await store.cached(), []);
});

test('redirected cache root or version directory is refused without writing through to its target', async (t) => {
  const { root, source, body } = await fixture(t);
  const outside = path.join(root, 'outside');
  await fs.mkdir(outside);
  const redirect = path.join(root, 'redirected-cache');
  if (!(await linkOrSkip(t, outside, redirect))) return;
  const redirectedStore = new UpdateStore(redirect);
  await assert.rejects(redirectedStore.removeLegacyAccess(), /redirected/);
  await assert.rejects(
    redirectedStore.cache('1.2.4', source, sha(body), body.length),
    /redirected/,
  );
  assert.deepEqual(await fs.readdir(outside), []);

  const store = new UpdateStore(path.join(root, 'second-cache'));
  await fs.mkdir(path.join(store.root, 'installers'), { recursive: true });
  if (!(await linkOrSkip(t, outside, path.join(store.root, 'installers', '1.2.4')))) return;
  assert.deepEqual(await store.cached(), []);
  await assert.rejects(store.cache('1.2.4', source, sha(body), body.length), /redirected/);
  assert.deepEqual(await fs.readdir(outside), []);
});

test('multiply linked source and cached installer cannot pass integrity verification or become an install choice', async (t) => {
  const { store, source, body, root } = await fixture(t);
  const value = await store.cache('1.2.4', source, sha(body), body.length);
  await fs.link(value.installerPath, path.join(root, 'retained-installer-link.exe'));
  await assert.rejects(store.verify(value), /invalid/);
  assert.deepEqual(await store.cached(), []);
  await fs.link(source, path.join(root, 'retained-download-link.exe'));
  await assert.rejects(store.cache('1.2.5', source, sha(body), body.length), /invalid/);
  assert.deepEqual(await fs.readFile(source), body);
});

test('multiply linked receipt files are never followed', async (t) => {
  const { store, source, body, root } = await fixture(t);
  const value = await store.cache('1.2.4', source, sha(body), body.length);
  await fs.link(
    path.join(path.dirname(value.installerPath), 'receipt.json'),
    path.join(root, 'retained-receipt.json'),
  );
  assert.deepEqual(await store.cached(), []);
});

test('channel preference defaults to stable and persists atomically independently of installer history', async (t) => {
  const { store } = await fixture(t);
  assert.equal(await store.readChannel(), 'stable');
  await store.saveChannel('test');
  assert.equal(await store.readChannel(), 'test');
  const reopened = new UpdateStore(store.root);
  assert.equal(await reopened.readChannel(), 'test');
  await store.saveChannel('stable');
  assert.equal(await reopened.readChannel(), 'stable');
  assert.deepEqual(
    (await fs.readdir(store.root)).filter((name) => name.endsWith('.tmp')),
    [],
  );
  assert.deepEqual(await store.cached(), []);
});

test('invalid, redirected or multiply linked channel preferences never enable test releases or modify linked data', async (t) => {
  const { store, root } = await fixture(t);
  await store.saveChannel('stable');
  const file = path.join(store.root, 'update-channel.json');
  for (const content of [
    'not-json',
    '{}',
    '{"schemaVersion":2,"channel":"test"}',
    '{"schemaVersion":1,"channel":"beta"}',
    '{"schemaVersion":1,"channel":"test","extra":true}',
  ]) {
    await fs.writeFile(file, content);
    await assert.rejects(store.readChannel(), /update channel/);
  }
  await store.saveChannel('test');
  const retained = path.join(root, 'retained-channel.json');
  await fs.link(file, retained);
  const bytes = await fs.readFile(retained);
  await assert.rejects(store.readChannel(), /update channel/);
  await assert.rejects(store.saveChannel('stable'), /invalid/);
  assert.deepEqual(await fs.readFile(retained), bytes);
});

test('test installers retain their complete version as independent cache and receipt identities', async (t) => {
  const { store, source, body } = await fixture(t);
  const first = await store.cache('1.3.0-test.2.9.1', source, sha(body), body.length);
  const next = await store.cache('1.3.0-test.2.10.1', source, sha(body), body.length);
  const stable = await store.cache('1.3.0', source, sha(body), body.length);
  assert.notEqual(first.installerPath, next.installerPath);
  assert.notEqual(next.installerPath, stable.installerPath);
  assert.deepEqual(
    new Set((await store.cached()).map((item) => item.version)),
    new Set(['1.3.0-test.2.9.1', '1.3.0-test.2.10.1', '1.3.0']),
  );
  for (const item of [first, next, stable]) await store.verify(item);
});

test('selecting a channel repairs an empty or oversized owned preference without requiring manual cleanup', async (t) => {
  const { store } = await fixture(t);
  await store.saveChannel('stable');
  const file = path.join(store.root, 'update-channel.json');
  for (const bytes of ['', 'x'.repeat(2048)]) {
    await fs.writeFile(file, bytes);
    await assert.rejects(store.readChannel(), /Select the channel again/);
    await store.saveChannel('stable');
    assert.equal(await store.readChannel(), 'stable');
  }
});

test('retired credential cleanup removes owned legacy bytes without reading or decrypting them', async (t) => {
  const { store } = await fixture(t);
  await store.removeLegacyAccess();
  await fs.mkdir(store.root, { recursive: true });
  const legacy = path.join(store.root, 'github-token.bin');
  for (const bytes of [
    Buffer.alloc(0),
    Buffer.from('Invalid retired ciphertext'),
    Buffer.alloc(32768, 0xa5),
  ]) {
    await fs.writeFile(legacy, bytes);
    await store.removeLegacyAccess();
    await assert.rejects(fs.access(legacy), { code: 'ENOENT' });
  }
  assert.equal(await store.readChannel(), 'stable');
  assert.deepEqual(await store.cached(), []);
});

test('retired credential cleanup preserves multiply linked files and their external bytes', async (t) => {
  const { store, root } = await fixture(t);
  await fs.mkdir(store.root, { recursive: true });
  const retained = path.join(root, 'retained-legacy.bin');
  const bytes = Buffer.from('Retained credential fixture, never decrypted');
  await fs.writeFile(retained, bytes);
  const legacy = path.join(store.root, 'github-token.bin');
  await fs.link(retained, legacy);
  await store.removeLegacyAccess();
  assert.deepEqual(await fs.readFile(retained), bytes);
  assert.deepEqual(await fs.readFile(legacy), bytes);
});

test('retired credential cleanup never follows a redirected entry', async (t) => {
  const { store, root } = await fixture(t);
  await fs.mkdir(store.root, { recursive: true });
  const outside = path.join(root, 'retained-directory');
  await fs.mkdir(outside);
  const retained = path.join(outside, 'retained.bin');
  const bytes = Buffer.from('Unrelated retained fixture');
  await fs.writeFile(retained, bytes);
  const legacy = path.join(store.root, 'github-token.bin');
  if (!(await linkOrSkip(t, outside, legacy))) return;
  await store.removeLegacyAccess();
  assert.deepEqual(await fs.readFile(retained), bytes);
  assert.equal((await fs.lstat(legacy)).isSymbolicLink(), true);
});
