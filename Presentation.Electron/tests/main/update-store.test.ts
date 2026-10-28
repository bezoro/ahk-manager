import test, { type TestContext } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { UpdateStore, type CachedInstaller, type TokenEncryption } from '../../src/main/update-store.js';

const fakeToken = 'ghp_fixture_only_not_a_real_credential_12345';
const sha = (bytes: Buffer | string): string => createHash('sha256').update(bytes).digest('hex');

function encryption(enabled = true): TokenEncryption {
  return {
    available: () => enabled,
    encrypt: value => Buffer.concat([Buffer.from('FIXTURE-CIPHER:'), Buffer.from(value).map(byte => byte ^ 0xa5)]),
    decrypt: value => {
      if (value.subarray(0, 15).toString() !== 'FIXTURE-CIPHER:') throw new Error('Fixture ciphertext invalid.');
      return Buffer.from(value.subarray(15)).map(byte => byte ^ 0xa5).toString();
    },
  };
}

async function fixture(t: TestContext) {
  const parent = process.env.AHK_DIAGNOSTICS_TEST_ROOT;
  assert.ok(parent && path.isAbsolute(parent), 'The test dispatcher must provide its isolated staging root.');
  const root = await fs.mkdtemp(path.join(parent, 'update-store-'));
  t.after(async () => { await fs.rm(root, { recursive: true, force: true }); });
  const store = new UpdateStore(path.join(root, 'cache'), encryption());
  const source = path.join(root, 'download with espaço.exe');
  const body = Buffer.from('Owned installer fixture bytes. Never executed.');
  await fs.writeFile(source, body);
  return { root, store, source, body };
}

async function linkOrSkip(t: TestContext, target: string, link: string): Promise<boolean> {
  try { await fs.symlink(target, link, process.platform === 'win32' ? 'junction' : 'dir'); return true; }
  catch (error) {
    if (['EPERM', 'EACCES', 'ENOTSUP'].includes((error as NodeJS.ErrnoException).code ?? '')) { t.skip('The runner cannot create the owned redirect fixture.'); return false; }
    throw error;
  }
}

test('fake credentials are encrypted on disk, round-trip and rotate without plaintext or temporary leftovers', async t => {
  const { store } = await fixture(t); assert.equal(await store.readToken(), null);
  await store.saveToken(fakeToken);
  const file = path.join(store.root, 'github-token.bin'); const bytes = await fs.readFile(file);
  assert.equal(bytes.includes(Buffer.from(fakeToken)), false); assert.equal(await store.readToken(), fakeToken);
  const rotated = 'github_pat_fixture_only_not_real_987654321';
  await store.saveToken(rotated); assert.equal(await store.readToken(), rotated);
  assert.equal((await fs.readFile(file)).includes(Buffer.from(rotated)), false);
  assert.deepEqual((await fs.readdir(store.root)).filter(name => name.endsWith('.tmp')), []);
  if (process.platform !== 'win32') assert.equal((await fs.stat(file)).mode & 0o777, 0o600);
  await store.saveToken(null); assert.equal(await store.readToken(), null);
});

test('unavailable credential protection refuses plaintext fallback and leaves no credential file', async t => {
  const { root } = await fixture(t); const store = new UpdateStore(path.join(root, 'unprotected-cache'), encryption(false));
  await assert.rejects(store.saveToken(fakeToken), /não foi salvo/);
  await assert.rejects(fs.access(path.join(store.root, 'github-token.bin')), { code: 'ENOENT' });
  assert.equal(await store.readToken(), null);
});

test('invalid or corrupt credentials fail without returning the ciphertext or plaintext in error messages', async t => {
  const { store } = await fixture(t);
  for (const value of ['short', 'contains a space but otherwise sufficiently long', 'a'.repeat(513), 'a'.repeat(25) + '\0']) {
    await assert.rejects(store.saveToken(value), /token do GitHub válido/);
  }
  await fs.writeFile(path.join(store.root, 'github-token.bin'), fakeToken);
  await assert.rejects(store.readToken(), error => error instanceof Error && !error.message.includes(fakeToken) && /acesso protegido/.test(error.message));
});

test('real installer bytes and receipt hashes are verified before becoming a cached version', async t => {
  const { store, source, body } = await fixture(t);
  const value = await store.cache('1.2.4', source, sha(body), body.length);
  assert.deepEqual(await fs.readFile(value.installerPath), body); await store.verify(value);
  const receipt = JSON.parse(await fs.readFile(path.join(path.dirname(value.installerPath), 'receipt.json'), 'utf8'));
  assert.deepEqual(receipt, { schemaVersion: 1, updateProtocol: 1, version: '1.2.4', sha256: sha(body), bytes: body.length });
  assert.deepEqual(await store.cached(), [value]);
  assert.deepEqual(await fs.readFile(source), body);
});

test('wrong hash, byte count, empty input and path-shaped version never produce an installer choice', async t => {
  const { store, source, body, root } = await fixture(t);
  await assert.rejects(store.cache('1.2.4', source, '0'.repeat(64), body.length), /integridade/);
  await assert.rejects(store.cache('1.2.4', source, sha(body), body.length + 1), /integridade/);
  for (const version of ['../1.2.4', '1.2', '01.2.4', 'v1.2.4', '1.2.4\0', '1.2.4-beta']) await assert.rejects(store.cache(version, source, sha(body), body.length), /inválida/);
  const empty = path.join(root, 'empty.exe'); await fs.writeFile(empty, '');
  await assert.rejects(store.cache('1.2.4', empty, sha(''), 0), /inválido/);
  assert.deepEqual(await store.cached(), []); assert.deepEqual(await fs.readFile(source), body);
});

test('changed cached bytes are refused and a verified replacement preserves the corrupt evidence', async t => {
  const { store, source, body } = await fixture(t); const value = await store.cache('1.2.4', source, sha(body), body.length);
  const corrupt = Buffer.alloc(body.length, 0x58); await fs.writeFile(value.installerPath, corrupt);
  await assert.rejects(store.verify(value), /integridade/);
  const repaired = await store.cache('1.2.4', source, sha(body), body.length); await store.verify(repaired);
  const directory = path.dirname(value.installerPath);
  const preserved = (await fs.readdir(directory)).filter(name => name.startsWith('preserved-') && name.endsWith('.exe'));
  assert.equal(preserved.length, 1); assert.deepEqual(await fs.readFile(path.join(directory, preserved[0]!)), corrupt);
  assert.deepEqual(await fs.readFile(repaired.installerPath), body);
});

test('valid repeated cache operation is idempotent and offline verification refuses an out-of-history path', async t => {
  const { store, source, body, root } = await fixture(t); const first = await store.cache('1.2.4', source, sha(body), body.length);
  const before = await fs.stat(first.installerPath); const second = await store.cache('1.2.4', source, sha(body), body.length);
  assert.deepEqual(second, first); assert.equal((await fs.stat(first.installerPath)).mtimeMs, before.mtimeMs);
  assert.deepEqual((await fs.readdir(path.dirname(first.installerPath))).filter(name => name.startsWith('preserved-')), []);
  const outside: CachedInstaller = { ...first, installerPath: path.join(root, 'outside.exe') };
  await fs.writeFile(outside.installerPath, body); await assert.rejects(store.verify(outside), /fora do histórico/);
});

test('incomplete receipts and changed sizes are omitted from executable history', async t => {
  const { store, source, body } = await fixture(t); const value = await store.cache('1.2.4', source, sha(body), body.length);
  const receipt = path.join(path.dirname(value.installerPath), 'receipt.json');
  for (const document of ['not-json', '{}', JSON.stringify({ schemaVersion: 1, updateProtocol: 2, version: '1.2.4', sha256: sha(body) }), JSON.stringify({ schemaVersion: 1, updateProtocol: 1, version: '1.2.5', sha256: sha(body) })]) {
    await fs.writeFile(receipt, document); assert.deepEqual(await store.cached(), []);
  }
  await fs.writeFile(receipt, JSON.stringify({ schemaVersion: 1, updateProtocol: 1, version: '1.2.4', sha256: sha(body), bytes: body.length + 1 }));
  assert.deepEqual(await store.cached(), []);
});

test('redirected cache root or version directory is refused without writing through to its target', async t => {
  const { root, source, body } = await fixture(t);
  const outside = path.join(root, 'outside'); await fs.mkdir(outside);
  const redirect = path.join(root, 'redirected-cache'); if (!await linkOrSkip(t, outside, redirect)) return;
  const redirectedStore = new UpdateStore(redirect, encryption());
  await assert.rejects(redirectedStore.saveToken(fakeToken), /redirecionada/);
  await assert.rejects(redirectedStore.cache('1.2.4', source, sha(body), body.length), /redirecionada/);
  assert.deepEqual(await fs.readdir(outside), []);

  const store = new UpdateStore(path.join(root, 'second-cache'), encryption());
  await fs.mkdir(path.join(store.root, 'installers'), { recursive: true });
  if (!await linkOrSkip(t, outside, path.join(store.root, 'installers', '1.2.4'))) return;
  assert.deepEqual(await store.cached(), []);
  await assert.rejects(store.cache('1.2.4', source, sha(body), body.length), /redirecionada/);
  assert.deepEqual(await fs.readdir(outside), []);
});

test('multiply linked source and cached installer cannot pass integrity verification or become an install choice', async t => {
  const { store, source, body, root } = await fixture(t);
  const value = await store.cache('1.2.4', source, sha(body), body.length);
  await fs.link(value.installerPath, path.join(root, 'retained-installer-link.exe'));
  await assert.rejects(store.verify(value), /inválido/); assert.deepEqual(await store.cached(), []);
  await fs.link(source, path.join(root, 'retained-download-link.exe'));
  await assert.rejects(store.cache('1.2.5', source, sha(body), body.length), /inválido/);
  assert.deepEqual(await fs.readFile(source), body);
});

test('multiply linked credential and receipt files are never followed or removed', async t => {
  const { store, source, body, root } = await fixture(t); await store.saveToken(fakeToken);
  const tokenFile = path.join(store.root, 'github-token.bin'); const retained = path.join(root, 'retained-ciphertext.bin');
  await fs.link(tokenFile, retained); const ciphertext = await fs.readFile(retained);
  await assert.rejects(store.readToken(), /acesso protegido/); await assert.rejects(store.saveToken(null), /inválido/);
  assert.deepEqual(await fs.readFile(retained), ciphertext);
  const value = await store.cache('1.2.4', source, sha(body), body.length);
  await fs.link(path.join(path.dirname(value.installerPath), 'receipt.json'), path.join(root, 'retained-receipt.json'));
  assert.deepEqual(await store.cached(), []);
});
