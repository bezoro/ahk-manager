import test, { type TestContext } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { UpdateStore, type CachedPackage } from '../../src/main/updates/update-store.js';

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
  const source = path.join(root, 'download with espaço.zip');
  const body = Buffer.from('Owned archive fixture bytes. Never executed.');
  await fs.writeFile(source, body);
  return { root, store, source, body };
}

/** Construct the signed-manifest shape beside inert archive bytes before caching. */
async function cache(
  store: UpdateStore,
  version: string,
  source: string,
  sha256: string,
  bytes: number,
) {
  const body = await fs.readFile(source);
  await fs.writeFile(
    path.join(path.dirname(source), 'release.json'),
    JSON.stringify({
      schemaVersion: 1,
      updateProtocol: 2,
      version,
      commit: 'a'.repeat(40),
      publishedAt: '2026-11-06T00:00:00.000Z',
      notes: 'Portable cache fixture.',
      archive: {
        name: `AHK-Manager-${version}-Portable-x64.zip`,
        sha256: sha(body),
        sha512: createHash('sha512').update(body).digest('base64'),
        bytes: body.length,
      },
    }),
  );
  return store.cache(version, source, sha256, bytes);
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

test('real archive bytes and receipt hashes are verified before becoming a cached version', async (t) => {
  const { store, source, body } = await fixture(t);
  const value = await cache(store, '1.2.4', source, sha(body), body.length);
  assert.deepEqual(await fs.readFile(value.archivePath), body);
  await store.verify(value);
  const receipt = JSON.parse(
    await fs.readFile(path.join(path.dirname(value.archivePath), 'receipt.json'), 'utf8'),
  );
  assert.deepEqual(receipt, {
    schemaVersion: 1,
    updateProtocol: 2,
    version: '1.2.4',
    sha256: sha(body),
    bytes: body.length,
  });
  assert.deepEqual(await store.cached(), [value]);
  assert.deepEqual(await fs.readFile(source), body);
});

test('wrong hash, byte count, empty input and path-shaped version never produce an archive choice', async (t) => {
  const { store, source, body, root } = await fixture(t);
  await assert.rejects(cache(store, '1.2.4', source, '0'.repeat(64), body.length), /integrity/);
  await assert.rejects(cache(store, '1.2.4', source, sha(body), body.length + 1), /integrity/);
  for (const version of ['../1.2.4', '1.2', '01.2.4', 'v1.2.4', '1.2.4\0', '1.2.4-beta'])
    await assert.rejects(cache(store, version, source, sha(body), body.length), /invalid/);
  const empty = path.join(root, 'empty.zip');
  await fs.writeFile(empty, '');
  await assert.rejects(cache(store, '1.2.4', empty, sha(''), 0), /invalid/);
  assert.deepEqual(await store.cached(), []);
  assert.deepEqual(await fs.readFile(source), body);
});

test('changed cached bytes are refused and a verified replacement preserves the corrupt evidence', async (t) => {
  const { store, source, body } = await fixture(t);
  const value = await cache(store, '1.2.4', source, sha(body), body.length);
  const corrupt = Buffer.alloc(body.length, 0x58);
  await fs.writeFile(value.archivePath, corrupt);
  await assert.rejects(store.verify(value), /integrity/);
  const repaired = await cache(store, '1.2.4', source, sha(body), body.length);
  await store.verify(repaired);
  const directory = path.dirname(value.archivePath);
  const preserved = (await fs.readdir(directory)).filter(
    (name) => name.startsWith('preserved-') && name.endsWith('.zip'),
  );
  assert.equal(preserved.length, 1);
  assert.deepEqual(await fs.readFile(path.join(directory, preserved[0]!)), corrupt);
  assert.deepEqual(await fs.readFile(repaired.archivePath), body);
});

test('valid repeated cache operation is idempotent and offline verification refuses an out-of-history path', async (t) => {
  const { store, source, body, root } = await fixture(t);
  const first = await cache(store, '1.2.4', source, sha(body), body.length);
  const before = await fs.stat(first.archivePath);
  const second = await cache(store, '1.2.4', source, sha(body), body.length);
  assert.deepEqual(second, first);
  assert.equal((await fs.stat(first.archivePath)).mtimeMs, before.mtimeMs);
  assert.deepEqual(
    (await fs.readdir(path.dirname(first.archivePath))).filter((name) =>
      name.startsWith('preserved-'),
    ),
    [],
  );
  const outside: CachedPackage = { ...first, archivePath: path.join(root, 'outside.zip') };
  await fs.writeFile(outside.archivePath, body);
  await assert.rejects(store.verify(outside), /outside the version history/);
});

test('incomplete receipts and changed sizes are omitted from executable history', async (t) => {
  const { store, source, body } = await fixture(t);
  const value = await cache(store, '1.2.4', source, sha(body), body.length);
  const receipt = path.join(path.dirname(value.archivePath), 'receipt.json');
  for (const document of [
    'not-json',
    '{}',
    JSON.stringify({ schemaVersion: 1, updateProtocol: 2, version: '1.2.4', sha256: sha(body) }),
    JSON.stringify({ schemaVersion: 1, updateProtocol: 2, version: '1.2.5', sha256: sha(body) }),
  ]) {
    await fs.writeFile(receipt, document);
    assert.deepEqual(await store.cached(), []);
  }
  await fs.writeFile(
    receipt,
    JSON.stringify({
      schemaVersion: 1,
      updateProtocol: 2,
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
    cache(redirectedStore, '1.2.4', source, sha(body), body.length),
    /redirected/,
  );
  assert.deepEqual(await fs.readdir(outside), []);

  const store = new UpdateStore(path.join(root, 'second-cache'));
  await fs.mkdir(path.join(store.root, 'installers'), { recursive: true });
  if (!(await linkOrSkip(t, outside, path.join(store.root, 'installers', '1.2.4')))) return;
  assert.deepEqual(await store.cached(), []);
  await assert.rejects(cache(store, '1.2.4', source, sha(body), body.length), /redirected/);
  assert.deepEqual(await fs.readdir(outside), []);
});

test('multiply linked source and cached archive cannot pass integrity verification or become an install choice', async (t) => {
  const { store, source, body, root } = await fixture(t);
  const value = await cache(store, '1.2.4', source, sha(body), body.length);
  await fs.link(value.archivePath, path.join(root, 'retained-archive-link.zip'));
  await assert.rejects(store.verify(value), /invalid/);
  assert.deepEqual(await store.cached(), []);
  await fs.link(source, path.join(root, 'retained-download-link.zip'));
  await assert.rejects(cache(store, '1.2.5', source, sha(body), body.length), /invalid/);
  assert.deepEqual(await fs.readFile(source), body);
});

test('multiply linked receipt files are never followed', async (t) => {
  const { store, source, body, root } = await fixture(t);
  const value = await cache(store, '1.2.4', source, sha(body), body.length);
  await fs.link(
    path.join(path.dirname(value.archivePath), 'receipt.json'),
    path.join(root, 'retained-receipt.json'),
  );
  assert.deepEqual(await store.cached(), []);
});

test('channel preference defaults to stable and persists atomically independently of archive history', async (t) => {
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

test('test archives retain their complete version as independent cache and receipt identities', async (t) => {
  const { store, source, body } = await fixture(t);
  const first = await cache(store, '1.3.0-test.2.9.1', source, sha(body), body.length);
  const next = await cache(store, '1.3.0-test.2.10.1', source, sha(body), body.length);
  const stable = await cache(store, '1.3.0', source, sha(body), body.length);
  assert.notEqual(first.archivePath, next.archivePath);
  assert.notEqual(next.archivePath, stable.archivePath);
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

test('existing 1.6.x portable history keeps its exact directory, receipt and manifest without migration writes', async (t) => {
  const { root, body, store } = await fixture(t);
  const directory = path.join(store.root, 'installers', '1.6.2');
  const archivePath = path.join(directory, 'archive.zip');
  await fs.mkdir(directory, { recursive: true });
  const manifest = JSON.stringify({
    schemaVersion: 1,
    updateProtocol: 2,
    version: '1.6.2',
    commit: 'a'.repeat(40),
    publishedAt: '2026-11-03T00:00:00.000Z',
    notes: 'Existing portable release.',
    archive: {
      name: 'AHK-Manager-1.6.2-Portable-x64.zip',
      bytes: body.length,
      sha256: sha(body),
      sha512: createHash('sha512').update(body).digest('base64'),
    },
  });
  const receipt = JSON.stringify({
    schemaVersion: 1,
    updateProtocol: 2,
    version: '1.6.2',
    sha256: sha(body),
    bytes: body.length,
  });
  await fs.writeFile(archivePath, body);
  await fs.writeFile(path.join(directory, 'release.json'), manifest);
  await fs.writeFile(path.join(directory, 'receipt.json'), receipt);
  const existing = new UpdateStore(path.join(root, 'cache'));
  const cached = await existing.cached();
  assert.equal(cached.length, 1);
  assert.equal(cached[0]!.archivePath, archivePath);
  await existing.verify(cached[0]!);
  assert.equal(await fs.readFile(path.join(directory, 'receipt.json'), 'utf8'), receipt);
  assert.equal(await fs.readFile(path.join(directory, 'release.json'), 'utf8'), manifest);
});

test('retired Setup protocol receipts are ignored without deleting their historical bytes', async (t) => {
  const { store } = await fixture(t);
  const directory = path.join(store.root, 'installers', '1.3.2');
  await fs.mkdir(directory, { recursive: true });
  const executable = path.join(directory, 'installer.exe');
  const bytes = Buffer.from('Historical Setup bytes, never executed.');
  await fs.writeFile(executable, bytes);
  await fs.writeFile(
    path.join(directory, 'receipt.json'),
    JSON.stringify({
      schemaVersion: 1,
      updateProtocol: 1,
      version: '1.3.2',
      sha256: sha(bytes),
      bytes: bytes.length,
    }),
  );
  assert.deepEqual(await store.cached(), []);
  await assert.rejects(
    store.verify({
      version: '1.3.2',
      archivePath: executable,
      sha256: sha(bytes),
      bytes: bytes.length,
    }),
    /outside/,
  );
  assert.deepEqual(await fs.readFile(executable), bytes);
});
