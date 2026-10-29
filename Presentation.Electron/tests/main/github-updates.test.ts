import test from 'node:test';
import assert from 'node:assert/strict';
import {
  assetUrl,
  compareVersions,
  GitHubUpdateClient,
  validUpdateVersion,
} from '../../src/main/github-updates.js';

const API = 'https://api.github.com/repos/bezoro/ahk-manager';
const LIST = `${API}/releases?per_page=100&page=1`;
const PRIVATE_SERVER_VALUE = 'private_server_fixture_value_1234567890';
const DATE = '2026-10-28T12:00:00.000Z';
const COMMIT = 'a'.repeat(40);
const SHA512 = Buffer.alloc(64, 0x5a).toString('base64');

function fixture(version = '1.2.7', id = 10) {
  const manifest = {
    schemaVersion: 1,
    updateProtocol: 1,
    version,
    commit: COMMIT,
    publishedAt: DATE,
    notes: 'Startup fix.',
    installer: {
      name: `AHK-Manager-${version}-Setup-x64.exe`,
      sha256: 'b'.repeat(64),
      sha512: SHA512,
      bytes: 123456,
    },
  };
  const release = {
    id,
    tag_name: `v${version}`,
    draft: false,
    prerelease: false,
    target_commitish: COMMIT,
    published_at: DATE,
    assets: [
      {
        id: id * 10,
        name: 'ahk-manager-release.json',
        size: Buffer.byteLength(JSON.stringify(manifest)),
        state: 'uploaded',
        url: 'https://untrusted.example/manifest',
      },
      {
        id: id * 10 + 1,
        name: manifest.installer.name,
        size: manifest.installer.bytes,
        state: 'uploaded',
        url: 'https://untrusted.example/installer.exe',
      },
    ],
  };
  return { manifest, release };
}

function clientFor(handler: (url: string, init: RequestInit) => Response | Promise<Response>) {
  const calls: Array<{ url: string; init: RequestInit }> = [];
  const fetcher = (async (input: string | URL | Request, init?: RequestInit) => {
    assert.equal(new Headers(init?.headers).has('authorization'), false);
    assert.equal(new Headers(init?.headers).has('cookie'), false);
    const url = String(input);
    calls.push({ url, init: init! });
    return handler(url, init!);
  }) as typeof fetch;
  return { client: new GitHubUpdateClient(fetcher), calls };
}

const json = (value: unknown, headers?: HeadersInit) =>
  new Response(JSON.stringify(value), { status: 200, headers });

test('versions are compared numerically and update protocol excludes old, prerelease and noncanonical versions', () => {
  assert.equal(compareVersions('1.2.10', '1.2.9'), 1);
  assert.equal(compareVersions('1.2.0', '1.2.0'), 0);
  assert.equal(compareVersions('1.1.99', '1.2.0'), -1);
  for (const version of ['1.2.0', '1.2.123', '2.0.0'])
    assert.equal(validUpdateVersion(version), true);
  for (const version of [
    '1.1.999',
    '1.2',
    '1.2.0-beta',
    '01.2.0',
    '../1.2.0',
    '1.2.2147483648',
    '1.2.0\0',
    null,
    123,
  ])
    assert.equal(validUpdateVersion(version), false);
  assert.throws(() => compareVersions('1.2.0;command', '1.2.0'));
  for (const id of [0, -1, 1.5, NaN, Infinity, Number.MAX_SAFE_INTEGER + 1])
    assert.throws(() => assetUrl(id));
});

test('only complete stable protocol releases are offered, sorted by version, using fixed asset API URLs', async () => {
  const first = fixture('1.2.9', 10),
    newest = fixture('1.2.10', 20),
    old = fixture('1.1.99', 30);
  const manifests = new Map([
    [assetUrl(100), first.manifest],
    [assetUrl(200), newest.manifest],
  ]);
  const { client, calls } = clientFor((url) =>
    url === LIST
      ? json([
          first.release,
          { ...newest.release, id: 21, draft: true },
          { ...newest.release, id: 22, prerelease: true },
          old.release,
          newest.release,
          { ...fixture('1.2.5', 40).release, assets: [] },
        ])
      : json(manifests.get(url)),
  );
  const result = await client.releases();
  assert.deepEqual(
    result.map((item) => item.version),
    ['1.2.10', '1.2.9'],
  );
  assert.deepEqual(result[0], {
    id: 'github:20',
    version: '1.2.10',
    publishedAt: DATE,
    notes: newest.manifest.notes,
    commit: COMMIT,
    assetId: 201,
    sha256: 'b'.repeat(64),
    sha512: SHA512,
    bytes: 123456,
    installerName: newest.manifest.installer.name,
  });
  assert.equal(result[1]!.metadataOnly, true);
  assert.equal(result[1]!.sha256, '');
  assert.deepEqual(
    calls.map((call) => call.url),
    [LIST, assetUrl(200)],
  );
  for (const call of calls) {
    const headers = new Headers(call.init.headers);
    assert.equal(headers.has('authorization'), false);
    assert.equal(call.init.credentials, 'omit');
    assert.equal(call.init.redirect, 'manual');
    assert.ok(call.init.signal instanceof AbortSignal);
  }
});

test('public asset redirects omit credentials and API-specific headers before the trusted CDN request', async () => {
  const { release, manifest } = fixture();
  const cdn =
    'https://release-assets.githubusercontent.com/github-production-release-asset/123?sig=opaque';
  const { client, calls } = clientFor((url) =>
    url === LIST
      ? json([release])
      : url === assetUrl(100)
        ? new Response(null, { status: 302, headers: { location: cdn } })
        : json(manifest),
  );
  assert.equal((await client.releases()).length, 1);
  assert.equal(calls.at(-1)!.url, cdn);
  const headers = new Headers(calls.at(-1)!.init.headers);
  for (const name of ['authorization', 'cookie', 'x-github-api-version', 'if-none-match'])
    assert.equal(headers.has(name), false);
  assert.equal(headers.get('accept'), 'application/octet-stream');
});

test('asset redirects reject HTTP, credentials, arbitrary origins, ports, API redirection and redirect loops before fetching them', async () => {
  for (const target of [
    'http://release-assets.githubusercontent.com/file',
    'https://user:pass@release-assets.githubusercontent.com/file',
    'https://untrusted.example/file',
    'https://release-assets.githubusercontent.com:8443/file',
    `${API}/releases/assets/999`,
    'https://release-assets.githubusercontent.com/file#fragment',
  ]) {
    const { release } = fixture();
    const { client, calls } = clientFor((url) =>
      url === LIST
        ? json([release])
        : new Response(null, { status: 302, headers: { location: target } }),
    );
    await assert.rejects(client.releases(), /invalid update metadata/);
    assert.equal(calls.length, 2, target);
  }
  const { release } = fixture();
  const { client, calls } = clientFor((url) =>
    url === LIST
      ? json([release])
      : new Response(null, {
          status: 302,
          headers: { location: 'https://objects.githubusercontent.com/loop' },
        }),
  );
  await assert.rejects(client.releases(), /invalid/);
  assert.equal(calls.length, 5);
});

test('ETag revalidation avoids downloading immutable manifests again', async () => {
  const { release, manifest } = fixture();
  let lists = 0,
    assets = 0;
  const { client } = clientFor((url, init) => {
    if (url === LIST) {
      lists++;
      const headers = new Headers(init.headers);
      if (lists === 2) {
        assert.equal(headers.get('if-none-match'), '"revision1"');
        return new Response(null, { status: 304 });
      }
      assert.equal(headers.has('if-none-match'), false);
      return json([release], { etag: '"revision1"' });
    }
    assets++;
    return json(manifest);
  });
  const first = await client.releases();
  assert.deepEqual(await client.releases(), first);
  assert.equal(assets, 1);
  assert.equal(lists, 2);
  assert.equal(assets, 1);
});

test('a public history of one hundred versions fetches only the newest manifest and resolves a previous version on demand', async () => {
  const history = Array.from({ length: 100 }, (_, index) => fixture(`1.2.${index}`, index + 1));
  const manifests = new Map(
    history.map((item) => [assetUrl(item.release.assets[0]!.id), item.manifest]),
  );
  const { client, calls } = clientFor((url, init) => {
    assert.equal(new Headers(init.headers).has('authorization'), false);
    if (url === LIST)
      return json(
        history.map((item) => ({
          ...item.release,
          body: `Public release notes ${item.manifest.version}`,
        })),
      );
    if (url === `${API}/releases?per_page=100&page=2`) return json([]);
    return json(manifests.get(url));
  });
  const result = await client.releases();
  assert.equal(result.length, 100);
  assert.equal(result[0]!.version, '1.2.99');
  assert.equal(result[0]!.metadataOnly, undefined);
  assert.equal(result.filter((item) => item.metadataOnly).length, 99);
  assert.equal(result[1]!.notes, 'Public release notes 1.2.98');
  assert.equal(result[1]!.commit, COMMIT);
  assert.equal(result[1]!.sha256, '');
  assert.equal(result[1]!.sha512, '');
  assert.deepEqual(
    calls.map((call) => call.url),
    [LIST, `${API}/releases?per_page=100&page=2`, assetUrl(1000)],
  );
  const older = await client.resolve('github:99');
  assert.equal(older.version, '1.2.98');
  assert.equal(older.sha256, 'b'.repeat(64));
  assert.equal(older.metadataOnly, undefined);
  assert.equal(calls.at(-1)!.url, assetUrl(990));
  const count = calls.length;
  assert.deepEqual(await client.resolve('github:99'), older);
  assert.equal(calls.length, count, 'A resolved immutable manifest is reused');
});

test('older manifests are checked before installation while unknown IDs cannot select arbitrary assets', async () => {
  const latest = fixture('1.2.8', 20),
    previous = fixture('1.2.7', 10);
  const { client, calls } = clientFor((url) =>
    url === LIST
      ? json([
          latest.release,
          { ...previous.release, target_commitish: 'main', body: 'a'.repeat(8100) + '\0' },
        ])
      : json(
          url === assetUrl(200)
            ? latest.manifest
            : {
                ...previous.manifest,
                installer: { ...previous.manifest.installer, sha256: 'bad' },
              },
        ),
  );
  await assert.rejects(client.resolve('github:10'), /checked history/);
  assert.equal(calls.length, 0);
  const history = await client.releases();
  assert.equal(history[1]!.metadataOnly, true);
  assert.equal(history[1]!.notes.length, 8000);
  assert.equal(history[1]!.commit, '');
  await assert.rejects(client.resolve('github:10'), /invalid/);
  const count = calls.length;
  for (const id of ['github:999', assetUrl(100), '../installer.exe'])
    await assert.rejects(client.resolve(id), /checked history/);
  assert.equal(calls.length, count);
});

test('replacement asset IDs cause the manifest to be fetched and checked again', async () => {
  const { release, manifest } = fixture();
  let lists = 0;
  const { client, calls } = clientFor((url) => {
    if (url === LIST) {
      lists++;
      return json([
        {
          ...release,
          assets: release.assets.map((asset, index) => ({
            ...asset,
            id: asset.id + (lists > 1 && index === 0 ? 100 : 0),
          })),
        },
      ]);
    }
    return json(manifest);
  });
  await client.releases();
  await client.releases();
  assert.ok(calls.some((call) => call.url === assetUrl(200)));
});

test('manifest identity, protocol, installer size, path and both checksum encodings are required', async () => {
  const mutations: Array<(value: ReturnType<typeof fixture>['manifest']) => unknown> = [
    (value) => ({ ...value, schemaVersion: 2 }),
    (value) => ({ ...value, updateProtocol: 0 }),
    (value) => ({ ...value, version: '1.2.8' }),
    (value) => ({ ...value, commit: 'invalid' }),
    (value) => ({ ...value, publishedAt: 'tomorrow' }),
    (value) => ({ ...value, publishedAt: '2026-02-31T12:00:00.000Z' }),
    (value) => ({ ...value, notes: 'a'.repeat(8001) }),
    (value) => ({ ...value, notes: 'a\0b' }),
    (value) => ({ ...value, extra: 'unrecognized' }),
    (value) => ({ ...value, installer: { ...value.installer, name: '../installer.exe' } }),
    (value) => ({ ...value, installer: { ...value.installer, bytes: value.installer.bytes + 1 } }),
    (value) => ({ ...value, installer: { ...value.installer, sha256: 'B'.repeat(64) } }),
    (value) => ({
      ...value,
      installer: { ...value.installer, sha512: Buffer.alloc(32).toString('base64') },
    }),
    (value) => ({ ...value, installer: { ...value.installer, sha512: 'A'.repeat(85) + 'B==' } }),
    (value) => ({
      ...value,
      installer: { ...value.installer, url: 'https://untrusted.example/setup.exe' },
    }),
  ];
  for (const mutate of mutations) {
    const { release, manifest } = fixture();
    const { client } = clientFor((url) =>
      url === LIST ? json([release]) : json(mutate(manifest)),
    );
    await assert.rejects(client.releases(), /invalid update metadata/);
  }
});

test('duplicate or ambiguous release identities and assets fail closed instead of silently hiding a newer version', async () => {
  const { release, manifest } = fixture();
  const rows = [
    [release, { ...release, id: 11 }],
    [{ ...release, assets: [...release.assets, release.assets[0]] }],
    [{ ...release, assets: [release.assets[0]] }],
    [
      {
        ...release,
        assets: release.assets.map((asset) => ({ ...asset, size: 1024 * 1024 * 1024 + 1 })),
      },
    ],
    [{ ...release, target_commitish: 'feature/unreviewed' }],
    [{ ...release, assets: release.assets.map((asset) => ({ ...asset, state: 'new' })) }],
  ];
  for (const list of rows) {
    const { client } = clientFor((url) => (url === LIST ? json(list) : json(manifest)));
    await assert.rejects(client.releases(), /invalid/);
  }
});

test('duplicate JSON keys, invalid UTF8 and oversized streamed or declared manifest bodies are refused', async () => {
  const { release, manifest } = fixture();
  const ordinary = JSON.stringify(manifest);
  const invalid = [
    () =>
      new Response(
        ordinary.replace('"schemaVersion":1', '"schemaVersion":1,"schema\\u0056ersion":1'),
      ),
    () => new Response(new Uint8Array([0xff, 0xfe, 0x7b, 0x7d])),
    () => new Response(ordinary, { headers: { 'content-length': String(64 * 1024 + 1) } }),
    () =>
      new Response(
        new ReadableStream<Uint8Array>({
          start(controller) {
            controller.enqueue(Buffer.from(ordinary));
            controller.enqueue(Buffer.alloc(64 * 1024, 0x20));
            controller.close();
          },
        }),
      ),
  ];
  for (const response of invalid) {
    const { client } = clientFor((url) => (url === LIST ? json([release]) : response()));
    await assert.rejects(client.releases(), /invalid/);
  }
});

test('HTTP failures and thrown transport errors never reveal private response content or URLs', async () => {
  for (const status of [401, 403, 404, 429, 500]) {
    const { client } = clientFor(
      () => new Response(`private=${PRIVATE_SERVER_VALUE} private server information`, { status }),
    );
    await assert.rejects(client.releases(), (error) => {
      assert.ok(error instanceof Error);
      assert.doesNotMatch(error.message, new RegExp(PRIVATE_SERVER_VALUE));
      assert.doesNotMatch(error.message, /private server information|https:\/\//);
      return true;
    });
  }
  const { client } = clientFor(() => {
    throw new Error(`https://private/${PRIVATE_SERVER_VALUE}`);
  });
  await assert.rejects(
    client.releases(),
    (error) =>
      error instanceof Error &&
      !error.message.includes(PRIVATE_SERVER_VALUE) &&
      /connection/.test(error.message),
  );
});

test('history pagination is fixed to three bounded pages and rejects oversized metadata', async () => {
  const { client, calls } = clientFor((url) =>
    url.endsWith('/releases/latest')
      ? new Response(null, { status: 404 })
      : json(Array.from({ length: 100 }, () => ({ draft: true }))),
  );
  assert.deepEqual(await client.releases(), []);
  assert.deepEqual(
    calls.map((call) => call.url),
    [
      ...[1, 2, 3].map((page) => `${API}/releases?per_page=100&page=${page}`),
      `${API}/releases/latest`,
    ],
  );
  for (const response of [
    () => json(Array.from({ length: 101 }, () => ({ draft: true }))),
    () => new Response('[]', { headers: { 'content-length': String(2 * 1024 * 1024 + 1) } }),
  ]) {
    const { client: invalid } = clientFor(response);
    await assert.rejects(invalid.releases(), /invalid/);
  }
});

test('test channel includes canonical test prereleases and stable history while stable excludes every preview', async () => {
  const stable = fixture('1.2.16', 16),
    preview = fixture('1.3.0-test.18.1.2', 18),
    later = fixture('1.3.0-test.19.1.2', 19);
  preview.release.prerelease = true;
  later.release.prerelease = true;
  const manifests = new Map(
    [stable, preview, later].map((item) => [assetUrl(item.release.assets[0]!.id), item.manifest]),
  );
  const { client } = clientFor((url) =>
    url === LIST
      ? json([
          stable.release,
          preview.release,
          later.release,
          { ...fixture('1.3.0-beta.1', 20).release, prerelease: true },
          { ...fixture('1.3.0-test.20.1.2', 21).release, prerelease: false },
          { ...fixture('1.3.1', 22).release, prerelease: true },
        ])
      : json(manifests.get(url)),
  );
  assert.deepEqual(
    (await client.releases()).map((item) => item.version),
    ['1.2.16'],
  );
  assert.deepEqual(
    (await client.releases('test')).map((item) => item.version),
    ['1.3.0-test.19.1.2', '1.3.0-test.18.1.2', '1.2.16'],
  );
  assert.equal((await client.resolve('github:18')).version, '1.3.0-test.18.1.2');
  assert.deepEqual(
    (await client.releases('stable')).map((item) => item.version),
    ['1.2.16'],
  );
  await assert.rejects(client.resolve('github:18'), /checked history/);
});

test('test manifest must pin the exact commit and cannot name main or mismatch its test tag', async () => {
  for (const mutation of ['main', 'version', 'commit']) {
    const { release, manifest } = fixture('1.3.0-test.18.1.2');
    release.prerelease = true;
    if (mutation === 'main') release.target_commitish = 'main';
    if (mutation === 'version') manifest.version = '1.3.0-test.19.1.2';
    if (mutation === 'commit') manifest.commit = 'c'.repeat(40);
    const { client } = clientFor((url) => (url === LIST ? json([release]) : json(manifest)));
    await assert.rejects(client.releases('test'), /invalid/);
  }
});

test('historical preview metadata requires an exact commit before it can become a cached installation choice', async () => {
  for (const target of ['main', 'feature/test', 'b'.repeat(39), 'B'.repeat(40), COMMIT + '\n']) {
    const latest = fixture('1.3.0-test.19.1.2', 19),
      previous = fixture('1.3.0-test.18.1.2', 18);
    latest.release.prerelease = true;
    previous.release.prerelease = true;
    previous.release.target_commitish = target;
    const { client, calls } = clientFor((url) =>
      url === LIST ? json([latest.release, previous.release]) : json(latest.manifest),
    );
    await assert.rejects(client.releases('test'), /invalid/);
    assert.equal(
      calls.length,
      1,
      'Unpinned historical metadata must fail before manifest loading.',
    );
    await assert.rejects(client.resolve('github:18'), /checked history/);
  }
});

test('a response from the former channel cannot repopulate the catalog after a switch', async () => {
  const stable = fixture('1.2.16', 16),
    preview = fixture('1.3.0-test.18.1.2', 18);
  preview.release.prerelease = true;
  let finish!: (response: Response) => void;
  const { client, calls } = clientFor((url) =>
    url === LIST && calls.length === 1
      ? new Promise<Response>((resolve) => {
          finish = resolve;
        })
      : url === LIST
        ? json([stable.release, preview.release])
        : json(stable.manifest),
  );
  const older = client.releases('test');
  const rejected = assert.rejects(older, /changed during the request/);
  await client.releases('stable');
  finish(json([preview.release]));
  await rejected;
  await assert.rejects(client.resolve('github:18'), /checked history/);
  assert.equal((await client.resolve('github:16')).version, '1.2.16');
});

test('the latest stable stays available when three hundred previews crowd the bounded history', async () => {
  const stable = fixture('1.2.16', 16);
  const previews = Array.from({ length: 300 }, (_, index) => ({
    ...fixture(`1.3.0-test.${index + 1}.1.2`, index + 100).release,
    prerelease: true,
  }));
  const { client, calls } = clientFor((url) => {
    if (url === `${API}/releases/latest`) return json(stable.release);
    const page = /[?&]page=(\d+)/.exec(url)?.[1];
    if (page) return json(previews.slice((Number(page) - 1) * 100, Number(page) * 100));
    return json(stable.manifest);
  });
  const result = await client.releases('stable');
  assert.deepEqual(
    result.map((item) => item.version),
    ['1.2.16'],
  );
  assert.deepEqual(
    calls.map((call) => call.url),
    [
      ...[1, 2, 3].map((page) => `${API}/releases?per_page=100&page=${page}`),
      `${API}/releases/latest`,
      assetUrl(160),
    ],
  );
});

test('a truncated history preserves the highest latest stable even when lower stable releases are recent', async () => {
  const highest = fixture('1.3.0', 30),
    lower = fixture('1.2.17', 17);
  const rows = Array.from({ length: 300 }, () => ({ draft: true }));
  rows[0] = lower.release;
  const { client } = clientFor((url) => {
    if (url === `${API}/releases/latest`) return json(highest.release);
    const page = /[?&]page=(\d+)/.exec(url)?.[1];
    if (page) return json(rows.slice((Number(page) - 1) * 100, Number(page) * 100));
    return json(highest.manifest);
  });
  assert.deepEqual(
    (await client.releases('stable')).map((item) => item.version),
    ['1.3.0', '1.2.17'],
  );
});
