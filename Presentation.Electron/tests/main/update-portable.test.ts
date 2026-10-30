import test, { type TestContext } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import { createHash } from 'node:crypto';
import {
  GitHubUpdateClient,
  type RemoteUpdateRelease,
} from '../../src/main/updates/github-updates.js';
import { UpdateStore } from '../../src/main/updates/update-store.js';
import { downloadPortableRelease } from '../../src/main/updates/portable-update-download.js';
import {
  verifyCachedPortablePackageSignature,
  verifyPortableManifestPolicy,
} from '../../src/main/updates/portable-update-signature.js';
import { readPortablePackageProof } from '../../src/main/updates/portable-package-proof.js';
import { resolveDeployment } from '../../src/main/storage/deployment.js';

const API = 'https://api.github.com/repos/bezoro/ahk-manager';
const CDN = 'https://release-assets.githubusercontent.com';
const commit = 'a'.repeat(40);
const digest = (bytes: Uint8Array, algorithm = 'sha256', encoding: 'hex' | 'base64' = 'hex') =>
  createHash(algorithm).update(bytes).digest(encoding);

function portable(
  version = '1.4.0-test.40.1.6',
  id = 10,
  body = Buffer.from('Portable archive fixture. Never executed.'),
) {
  const manifest = {
    schemaVersion: 1,
    updateProtocol: 2,
    version,
    commit,
    publishedAt: '2026-10-30T00:00:00.000Z',
    notes: 'Portable fixture release.',
    archive: {
      name: `AHK-Manager-${version}-Portable-x64.zip`,
      bytes: body.length,
      sha256: digest(body),
      sha512: digest(body, 'sha512', 'base64'),
    },
  };
  const text = JSON.stringify(manifest);
  const release: RemoteUpdateRelease = {
    id: `github:${id}`,
    version,
    publishedAt: manifest.publishedAt,
    commit,
    notes: manifest.notes,
    assetId: id * 10 + 1,
    installerName: manifest.archive.name,
    bytes: body.length,
    sha256: manifest.archive.sha256,
    sha512: manifest.archive.sha512,
    deployment: 'portable',
    manifestText: text,
  };
  const metadata = {
    id,
    tag_name: 'v' + version,
    draft: false,
    prerelease: version.includes('-test.'),
    target_commitish: commit,
    published_at: manifest.publishedAt,
    assets: [
      {
        id: id * 10,
        name: 'ahk-manager-portable-release.json',
        size: Buffer.byteLength(text),
        state: 'uploaded',
      },
      { id: release.assetId, name: manifest.archive.name, size: body.length, state: 'uploaded' },
    ],
  };
  return { body, manifest, text, release, metadata };
}

async function disk(t: TestContext) {
  const parent = process.env.AHK_DIAGNOSTICS_TEST_ROOT;
  assert.ok(parent && path.isAbsolute(parent));
  const root = await fs.mkdtemp(path.join(parent, 'portable-update-'));
  t.after(() => fs.rm(root, { recursive: true, force: true }));
  const store = new UpdateStore(path.join(root, 'Data', 'Updates'), 'portable');
  return { root, store };
}

function fixtureFetch(
  f: ReturnType<typeof portable>,
  response: () => Response,
  calls: string[] = [],
) {
  return (async (input, init) => {
    assert.equal(init?.method, 'GET');
    assert.equal(init?.redirect, 'manual');
    assert.equal(init?.credentials, 'omit');
    assert.equal(new Headers(init?.headers).has('authorization'), false);
    assert.equal(new Headers(init?.headers).has('cookie'), false);
    init?.signal?.throwIfAborted();
    const url = String(input);
    calls.push(url);
    if (url === `${API}/releases/assets/${f.release.assetId}`)
      return new Response(null, {
        status: 302,
        headers: { Location: `${CDN}/archive.zip?fixture=not-a-secret` },
      });
    if (url === `${CDN}/archive.zip?fixture=not-a-secret`) return response();
    if (url === `${API}/releases/assets/999`)
      return new Response(Buffer.from('Fixture signature bytes.'));
    throw new Error('Unexpected fixture request.');
  }) as typeof fetch;
}

test('moving the complete portable folder preserves external script references and verified update history', async (t) => {
  const { root } = await disk(t);
  const original = path.join(root, 'original');
  const relocated = path.join(root, 'relocated portable folder');
  const external = path.join(root, 'external scripts', 'independent.ahk');
  await fs.mkdir(path.join(original, 'App'), { recursive: true });
  await fs.mkdir(path.dirname(external), { recursive: true });
  await fs.writeFile(external, '; Independent script: never executed by this fixture.');
  await fs.writeFile(
    path.join(original, 'portable.json'),
    JSON.stringify({
      schemaVersion: 1,
      applicationId: 'Bezoro.AhkManager.Electron',
      dataDirectory: 'Data',
    }),
  );
  const before = resolveDeployment(
    path.join(original, 'App', 'Bezoro.AhkManager.exe'),
    path.join(root, 'local-app-data'),
  );
  await fs.mkdir(before.profileDirectory, { recursive: true });
  const settings = JSON.stringify({
    Scripts: [{ Path: external, StartWithWindows: true, RunAsAdministrator: true }],
    Settings: { Theme: 'Dark', LaunchManagerAtLogin: true, LaunchManagerAsAdministrator: true },
  });
  await fs.writeFile(path.join(before.profileDirectory, 'settings.json'), settings);
  const initialStore = new UpdateStore(before.updatesDirectory, 'portable');
  await initialStore.saveChannel('test');
  const f = portable();
  const downloaded = await downloadPortableRelease(f.release, () => {}, {
    store: initialStore,
    fetcher: fixtureFetch(f, () => new Response(f.body)),
    verifyManifest: async () => {},
  });
  await initialStore.cache(f.release.version, downloaded, f.release.sha256, f.release.bytes);

  await fs.rename(original, relocated);
  const after = resolveDeployment(
    path.join(relocated, 'App', 'Bezoro.AhkManager.exe'),
    path.join(root, 'local-app-data'),
  );
  assert.equal(after.launchExecutable, path.join(relocated, 'Bezoro.AhkManager.exe'));
  assert.equal(after.dataDirectory, path.join(relocated, 'Data'));
  assert.equal(
    await fs.readFile(path.join(after.profileDirectory, 'settings.json'), 'utf8'),
    settings,
  );
  assert.equal(
    await fs.readFile(external, 'utf8'),
    '; Independent script: never executed by this fixture.',
  );
  const store = new UpdateStore(after.updatesDirectory, 'portable');
  assert.equal(await store.readChannel(), 'test');
  const history = await store.cached();
  assert.equal(history.length, 1);
  assert.equal(history[0]!.version, f.release.version);
  assert.ok(history[0]!.installerPath.startsWith(after.updatesDirectory + path.sep));
  await store.verify(history[0]!);
  await store.saveChannel('stable');
  await assert.rejects(fs.lstat(original), { code: 'ENOENT' });
});

test('portable catalog selects only its own manifest and archive, retaining exact metadata for signatures', async () => {
  const f = portable();
  const older = portable('1.4.0', 20);
  const installerOnly = {
    ...older.metadata,
    id: 21,
    assets: older.metadata.assets.map((item) => ({
      ...item,
      name:
        item.name === 'ahk-manager-portable-release.json'
          ? 'ahk-manager-release.json'
          : item.name.replace('Portable-x64.zip', 'Setup-x64.exe'),
    })),
  };
  const metadata = {
    ...f.metadata,
    assets: [
      ...f.metadata.assets,
      { id: 999, name: 'ahk-manager-portable-release.json.p7s', size: 1024, state: 'uploaded' },
    ],
  };
  const fetcher = (async (input) => {
    const url = String(input);
    if (url === `${API}/releases?per_page=100&page=1`)
      return Response.json([metadata, installerOnly]);
    if (url === `${API}/releases/assets/100`) return new Response(f.text);
    throw new Error('Unexpected fixture request.');
  }) as typeof fetch;
  const client = new GitHubUpdateClient(fetcher, 'portable');
  const choices = await client.releases('test');
  assert.equal(choices.length, 1);
  assert.equal(choices[0]!.version, f.release.version);
  assert.equal(choices[0]!.manifestText, f.text);
  assert.equal(choices[0]!.deployment, 'portable');
  assert.equal(choices[0]!.signatureAssetId, 999);
  assert.deepEqual(await client.resolve(choices[0]!.id), choices[0]);
});

test('resolving the running version does not change the selected update catalog', async () => {
  const f = portable();
  const current = portable('1.4.0', 20);
  const fetcher = (async (input) => {
    const url = String(input);
    if (url === `${API}/releases?per_page=100&page=1`) return Response.json([f.metadata]);
    if (url === `${API}/releases/tags/v1.4.0`) return Response.json(current.metadata);
    if (url === `${API}/releases/assets/100`) return new Response(f.text);
    if (url === `${API}/releases/assets/200`) return new Response(current.text);
    throw new Error('Unexpected fixture request.');
  }) as typeof fetch;
  const client = new GitHubUpdateClient(fetcher, 'portable');
  await client.releases('test');
  assert.equal((await client.resolveVersion('1.4.0')).version, '1.4.0');
  assert.equal((await client.resolve(f.release.id)).version, f.release.version);
  await assert.rejects(client.resolveVersion('../1.4.0'), /metadata/);
});

test('portable manifest refuses installer protocol, duplicate fields and mismatched archive identities', () => {
  const f = portable();
  assert.equal(readPortablePackageProof(f.text).sha256, f.release.sha256);
  for (const document of [
    { ...f.manifest, updateProtocol: 1 },
    { ...f.manifest, installer: f.manifest.archive },
    { ...f.manifest, archive: { ...f.manifest.archive, name: 'installer.exe' } },
    { ...f.manifest, archive: { ...f.manifest.archive, bytes: 1024 * 1024 * 1024 + 1 } },
    { ...f.manifest, commit: 'main' },
    { ...f.manifest, publishedAt: '2026-02-30T00:00:00Z' },
  ])
    assert.throws(() => readPortablePackageProof(JSON.stringify(document)), /metadata/);
  assert.throws(
    () =>
      readPortablePackageProof(
        f.text.replace('"updateProtocol":2', '"updateProtocol":1,"updateProtocol":2'),
      ),
    /metadata/,
  );
});

test('portable streaming validates both hashes, verifies signature first and caches an isolated edition', async (t) => {
  const { root, store } = await disk(t);
  const f = portable();
  f.release.signatureAssetId = 999;
  const calls: string[] = [];
  const progress: number[] = [];
  let verified = false;
  const fetcher = fixtureFetch(
    f,
    () => {
      assert.equal(verified, true);
      return new Response(
        new ReadableStream<Uint8Array>({
          start(controller) {
            controller.enqueue(f.body.subarray(0, 5));
            controller.enqueue(f.body.subarray(5));
            controller.close();
          },
        }),
        { headers: { 'Content-Length': String(f.body.length) } },
      );
    },
    calls,
  );
  const source = await downloadPortableRelease(f.release, (percent) => progress.push(percent), {
    store,
    fetcher,
    verifyManifest: async (manifest, signature) => {
      assert.equal(await fs.readFile(manifest, 'utf8'), f.text);
      assert.equal(await fs.readFile(signature, 'utf8'), 'Fixture signature bytes.');
      verified = true;
    },
  });
  assert.deepEqual(await fs.readFile(source), f.body);
  const value = await store.cache(f.release.version, source, f.release.sha256, f.release.bytes);
  assert.equal(path.basename(value.installerPath), 'archive.zip');
  await store.verify(value);
  assert.equal(
    await fs.readFile(path.join(path.dirname(value.installerPath), 'release.json'), 'utf8'),
    f.text,
  );
  assert.equal(
    (
      JSON.parse(
        await fs.readFile(path.join(path.dirname(value.installerPath), 'receipt.json'), 'utf8'),
      ) as { updateProtocol: number }
    ).updateProtocol,
    2,
  );
  assert.deepEqual(await new UpdateStore(store.root).cached(), []);
  assert.deepEqual(await store.cached(), [value]);
  assert.equal(progress.at(-1), 100);
  assert.ok(progress.every((value, index) => index === 0 || value >= progress[index - 1]!));
  assert.ok(calls.every((url) => !url.includes('untrusted')));
  await assert.rejects(fs.access(source), { code: 'ENOENT' });
  await fs.writeFile(path.join(root, 'unrelated-script.ahk'), '; external reference');
  await store.verify(value);
  assert.equal(
    await fs.readFile(path.join(root, 'unrelated-script.ahk'), 'utf8'),
    '; external reference',
  );
});

test('truncated, oversized and corrupted streams never become portable history entries', async (t) => {
  for (const mode of ['truncated', 'oversized', 'corrupt', 'length', 'sha512'] as const) {
    const { store } = await disk(t);
    const f = portable();
    if (mode === 'sha512') {
      f.manifest.archive.sha512 = Buffer.alloc(64, 1).toString('base64');
      f.release.sha512 = f.manifest.archive.sha512;
      f.release.manifestText = JSON.stringify(f.manifest);
    }
    const body =
      mode === 'truncated'
        ? f.body.subarray(1)
        : mode === 'oversized'
          ? Buffer.concat([f.body, Buffer.from('extra')])
          : mode === 'corrupt'
            ? Buffer.alloc(f.body.length, 1)
            : f.body;
    await assert.rejects(
      downloadPortableRelease(f.release, () => {}, {
        store,
        fetcher: fixtureFetch(
          f,
          () =>
            new Response(
              body,
              mode === 'length' ? { headers: { 'Content-Length': '999' } } : undefined,
            ),
        ),
        verifyManifest: async () => {},
      }),
      /integrity|size|length/,
    );
    assert.deepEqual(await store.cached(), []);
    assert.deepEqual(await fs.readdir(path.join(store.root, 'downloads')), []);
  }
});

test('untrusted redirects and explicit cancellation leave no partial portable package', async (t) => {
  for (const mode of ['redirect', 'cancel'] as const) {
    const { store } = await disk(t);
    const f = portable();
    const signal =
      mode === 'cancel'
        ? AbortSignal.abort(new DOMException('Fixture cancellation.', 'AbortError'))
        : undefined;
    const fetcher = (async (_input, init) => {
      init?.signal?.throwIfAborted();
      return new Response(null, {
        status: 302,
        headers: { Location: 'https://untrusted.example/archive.zip' },
      });
    }) as typeof fetch;
    await assert.rejects(
      downloadPortableRelease(f.release, () => {}, {
        store,
        fetcher,
        signal,
        verifyManifest: async () => {},
      }),
      /untrusted|cancellation/,
    );
    assert.deepEqual(await store.cached(), []);
    assert.deepEqual(await fs.readdir(path.join(store.root, 'downloads')), []);
  }
});

test('publisher policy refuses a missing signature before downloading the archive', async (t) => {
  const { root, store } = await disk(t);
  const f = portable();
  const policy = path.join(root, 'app-update.yml');
  await fs.writeFile(policy, 'publisherName: Fixture Publisher\n');
  let downloads = 0;
  await assert.rejects(
    downloadPortableRelease(f.release, () => {}, {
      store,
      fetcher: (async () => {
        downloads++;
        throw new Error('Archive must not be requested.');
      }) as typeof fetch,
      verifyManifest: (manifest, signature) =>
        verifyPortableManifestPolicy(manifest, signature, { policyPath: policy }),
    }),
    /signature/,
  );
  assert.equal(downloads, 0);
  assert.deepEqual(await store.cached(), []);
});

test('signed cached rollback binds raw metadata, publisher policy and actual archive bytes', async (t) => {
  const { root, store } = await disk(t);
  const f = portable();
  const source = path.join(root, 'source');
  await fs.mkdir(source);
  await fs.writeFile(path.join(source, 'archive.zip'), f.body);
  await fs.writeFile(path.join(source, 'release.json'), f.text);
  await fs.writeFile(path.join(source, 'release.json.p7s'), 'Fixture signature bytes.');
  const value = await store.cache(
    f.release.version,
    path.join(source, 'archive.zip'),
    f.release.sha256,
    f.release.bytes,
  );
  const policy = path.join(root, 'app-update.yml');
  await fs.writeFile(policy, 'publisherName:\n  - Fixture Publisher\n');
  let checked = 0;
  const options = {
    policyPath: policy,
    verify: async (manifest: string, signature: string, publishers: string[]) => {
      checked++;
      assert.deepEqual(publishers, ['Fixture Publisher']);
      assert.equal(await fs.readFile(manifest, 'utf8'), f.text);
      assert.equal(await fs.readFile(signature, 'utf8'), 'Fixture signature bytes.');
      return digest(await fs.readFile(manifest));
    },
  };
  await verifyCachedPortablePackageSignature(value.installerPath, options);
  assert.equal(checked, 1);
  await fs.writeFile(value.installerPath, Buffer.alloc(f.body.length, 1));
  await assert.rejects(
    verifyCachedPortablePackageSignature(value.installerPath, options),
    /signed release metadata/,
  );
  assert.equal(checked, 1);
  await fs.writeFile(value.installerPath, f.body);
  await fs.unlink(path.join(path.dirname(value.installerPath), 'release.json.p7s'));
  await assert.rejects(
    verifyCachedPortablePackageSignature(value.installerPath, options),
    /signature/,
  );
});

test('publisher verification must authenticate the exact manifest bytes bound to the cached archive', async (t) => {
  const { root, store } = await disk(t);
  const f = portable();
  const source = path.join(root, 'source');
  await fs.mkdir(source);
  await fs.writeFile(path.join(source, 'archive.zip'), f.body);
  await fs.writeFile(path.join(source, 'release.json'), f.text);
  await fs.writeFile(path.join(source, 'release.json.p7s'), 'Fixture signature bytes.');
  const cached = await store.cache(
    f.release.version,
    path.join(source, 'archive.zip'),
    f.release.sha256,
    f.release.bytes,
  );
  const directory = path.dirname(cached.installerPath);
  const manifest = path.join(directory, 'release.json');
  const signature = path.join(directory, 'release.json.p7s');
  const policy = path.join(root, 'app-update.yml');
  await fs.writeFile(policy, 'publisherName: Fixture Publisher\n');
  const replacement = JSON.stringify({
    ...f.manifest,
    archive: { ...f.manifest.archive, sha256: 'b'.repeat(64) },
  });
  const options = {
    policyPath: policy,
    verify: async (file: string) => {
      // Model a valid CMS for replacement bytes while restoring the original
      // path contents before returning. Post-verification reads cannot bind it.
      await fs.writeFile(file, replacement);
      const authenticated = digest(await fs.readFile(file));
      await fs.writeFile(file, f.text);
      return authenticated;
    },
  };
  await assert.rejects(
    verifyCachedPortablePackageSignature(cached.installerPath, options),
    /metadata changed during publisher verification/,
  );
  await assert.rejects(
    verifyPortableManifestPolicy(manifest, signature, options),
    /metadata changed during publisher verification/,
  );
  assert.equal(await fs.readFile(manifest, 'utf8'), f.text);
  await store.verify(cached);
});

test('portable caches reject changed proof, setup receipts and proofless archives', async (t) => {
  const { root, store } = await disk(t);
  const f = portable();
  const source = path.join(root, 'source');
  await fs.mkdir(source);
  const file = path.join(source, 'archive.zip');
  await fs.writeFile(file, f.body);
  await assert.rejects(store.cache(f.release.version, file, f.release.sha256, f.release.bytes));
  await fs.writeFile(path.join(source, 'release.json'), f.text);
  const value = await store.cache(f.release.version, file, f.release.sha256, f.release.bytes);
  const directory = path.dirname(value.installerPath);
  const proof = JSON.parse(f.text) as typeof f.manifest;
  proof.archive.sha256 = 'b'.repeat(64);
  await fs.writeFile(path.join(directory, 'release.json'), JSON.stringify(proof));
  await assert.rejects(store.verify(value), /metadata/);
  assert.deepEqual(await store.cached(), []);
  await fs.writeFile(path.join(directory, 'release.json'), f.text);
  const receipt = JSON.parse(await fs.readFile(path.join(directory, 'receipt.json'), 'utf8')) as {
    updateProtocol: number;
  };
  receipt.updateProtocol = 1;
  await fs.writeFile(path.join(directory, 'receipt.json'), JSON.stringify(receipt));
  assert.deepEqual(await store.cached(), []);
});

test('downloading a signed replacement repairs proof for an already cached portable archive', async (t) => {
  const { root, store } = await disk(t);
  const f = portable();
  const source = path.join(root, 'source');
  await fs.mkdir(source);
  await fs.writeFile(path.join(source, 'archive.zip'), f.body);
  await fs.writeFile(path.join(source, 'release.json'), f.text);
  const cached = await store.cache(
    f.release.version,
    path.join(source, 'archive.zip'),
    f.release.sha256,
    f.release.bytes,
  );
  const policy = path.join(root, 'app-update.yml');
  await fs.writeFile(policy, 'publisherName: Fixture Publisher\n');
  f.release.signatureAssetId = 999;
  const options = {
    policyPath: policy,
    verify: async (manifest: string, signature: string) => {
      assert.equal(await fs.readFile(manifest, 'utf8'), f.text);
      assert.equal(await fs.readFile(signature, 'utf8'), 'Fixture signature bytes.');
      return digest(await fs.readFile(manifest));
    },
  };
  const directory = path.dirname(cached.installerPath);
  for (const damaged of ['missing', 'corrupt'] as const) {
    if (damaged === 'corrupt')
      await fs.writeFile(path.join(directory, 'release.json.p7s'), 'Corrupt fixture signature.');
    await assert.rejects(verifyCachedPortablePackageSignature(cached.installerPath, options));
    const downloaded = await downloadPortableRelease(f.release, () => {}, {
      store,
      fetcher: fixtureFetch(f, () => new Response(f.body)),
      verifyManifest: (manifest, signature) =>
        verifyPortableManifestPolicy(manifest, signature, options),
    });
    const repaired = await store.cache(
      f.release.version,
      downloaded,
      f.release.sha256,
      f.release.bytes,
    );
    assert.deepEqual(repaired, cached);
    await store.verify(repaired);
    await verifyCachedPortablePackageSignature(repaired.installerPath, options);
    assert.equal(
      (await fs.readdir(directory)).some((name) => name.startsWith('preserved-')),
      false,
    );
    await assert.rejects(fs.access(downloaded), { code: 'ENOENT' });
    assert.deepEqual(await fs.readdir(path.join(store.root, 'downloads')), []);
  }
});
