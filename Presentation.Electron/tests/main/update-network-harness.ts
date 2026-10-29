import { app, net, session } from 'electron';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { GitHubUpdateClient } from '../../src/main/github-updates.js';
import { createElectronUpdateFetch } from '../../src/main/electron-update-fetch.js';
import { configureUpdateNetwork } from '../../src/main/update-network-policy.js';

const argument = process.argv.indexOf('--output');
const output = process.argv[argument + 1];
if (argument < 0 || !output || !path.isAbsolute(output) || fs.existsSync(output))
  throw new Error('Use a fresh absolute network test output directory.');
fs.mkdirSync(output, { recursive: true });
const profile = path.join(output, 'profile');
fs.mkdirSync(profile);
app.setPath('userData', profile);
app.commandLine.appendSwitch('disable-gpu');

const API = 'https://api.github.com/repos/bezoro/ahk-manager/releases';
const CDN = 'https://release-assets.githubusercontent.com';
const legacyCommit = 'ff035abbf8fb53348a38cbd60e36287a82f5ec6f';
const checks: Array<{ name: string; passed: boolean }> = [];
const failures: string[] = [];
const requests: Array<{
  stage: string;
  origin: string;
  pathname: string;
  authorization: boolean;
  cookie: boolean;
}> = [];
let stage = 'setup';
let mode: 'normal' | 'untrusted' | 'loop' | 'oversized' | 'delayed' = 'normal';
let legacyCause: string | null = null;

function fixture(version: string, id: number, prerelease = false) {
  const manifest = {
    schemaVersion: 1,
    updateProtocol: 1,
    version,
    commit: 'a'.repeat(40),
    publishedAt: '2026-10-29T00:00:00.000Z',
    notes: 'Network fixture release.',
    installer: {
      name: `AHK-Manager-${version}-Setup-x64.exe`,
      sha256: 'b'.repeat(64),
      sha512: Buffer.alloc(64, 90).toString('base64'),
      bytes: 123456,
    },
  };
  const release = {
    id,
    tag_name: `v${version}`,
    draft: false,
    prerelease,
    target_commitish: manifest.commit,
    published_at: manifest.publishedAt,
    body: manifest.notes,
    assets: [
      {
        id: id * 10,
        name: 'ahk-manager-release.json',
        size: Buffer.byteLength(JSON.stringify(manifest)),
        state: 'uploaded',
      },
      {
        id: id * 10 + 1,
        name: manifest.installer.name,
        size: manifest.installer.bytes,
        state: 'uploaded',
      },
    ],
  };
  return { manifest, release };
}
const stable = fixture('1.3.1', 11);
const previous = fixture('1.3.0', 10);
const preview = fixture('1.3.2-test.40.1.2', 12, true);
const releases = [stable.release, previous.release, preview.release];
const manifests = new Map(
  [stable, previous, preview].map((item) => [String(item.release.assets[0]!.id), item.manifest]),
);

async function check(name: string, action: () => Promise<void>) {
  stage = name;
  try {
    await action();
    checks.push({ name, passed: true });
  } catch (error) {
    checks.push({ name, passed: false });
    failures.push(`${name}: ${error instanceof Error ? error.message : 'Unexpected failure.'}`);
  }
}

const deadline = setTimeout(() => {
  failures.push('The isolated network harness exceeded its deadline.');
  fs.writeFileSync(
    path.join(output, 'summary.json'),
    JSON.stringify({ fixture: true, checks, failures, legacyCause }),
  );
  app.exit(1);
}, 60000);

app
  .whenReady()
  .then(async () => {
    // The renderer session stays blocked while the update session performs its requests.
    session.defaultSession.webRequest.onBeforeRequest((_details, callback) =>
      callback({ cancel: true }),
    );
    const network = session.fromPartition('electron-updater', { cache: false });
    configureUpdateNetwork(network);
    await network.protocol.handle('https', async (request) => {
      const url = new URL(request.url);
      requests.push({
        stage,
        origin: url.origin,
        pathname: url.pathname,
        authorization: request.headers.has('authorization'),
        cookie: request.headers.has('cookie'),
      });
      if (mode === 'delayed') await new Promise<void>((resolve) => setTimeout(resolve, 250));
      if (
        url.origin === 'https://api.github.com' &&
        url.pathname === '/repos/bezoro/ahk-manager/releases'
      ) {
        if (mode === 'oversized')
          return new Response(' '.repeat(2 * 1024 * 1024 + 1), {
            headers: { 'Content-Length': String(2 * 1024 * 1024 + 1) },
          });
        if (request.headers.get('if-none-match') === 'network-fixture-etag')
          return new Response(null, { status: 304 });
        return Response.json(releases, { headers: { ETag: 'network-fixture-etag' } });
      }
      const asset = /\/releases\/assets\/(\d+)$/.exec(url.pathname)?.[1];
      if (url.origin === 'https://api.github.com' && asset && manifests.has(asset))
        return new Response(null, {
          status: 302,
          headers: {
            Location:
              mode === 'untrusted'
                ? 'https://untrusted.example/fixture.json'
                : `${CDN}/${asset}.json`,
          },
        });
      if (url.origin === CDN) {
        if (mode === 'loop')
          return new Response(null, { status: 302, headers: { Location: `${CDN}/loop.json` } });
        const id = /^\/(\d+)\.json$/.exec(url.pathname)?.[1];
        if (id && manifests.has(id)) return Response.json(manifests.get(id));
      }
      return new Response('Unexpected fixture request.', { status: 404 });
    });
    const fetcher = createElectronUpdateFetch(network, (options) => net.request(options));
    await network.cookies.set({
      url: 'https://api.github.com',
      name: 'isolated-fixture-cookie',
      value: 'not-a-credential',
      secure: true,
    });

    await check('the locked renderer session cannot reach GitHub', async () => {
      await assert.rejects(
        session.defaultSession.fetch(API, { signal: AbortSignal.timeout(3000) }),
      );
    });
    await check(
      'the production policy blocks sibling routes, other repositories and non-HTTPS origins',
      async () => {
        for (const url of [
          `${API}-unrelated`,
          'https://api.github.com/repos/another/repository/releases',
          API.replace('https:', 'http:'),
          'https://untrusted.example/releases',
          'https://api.github.com:444/repos/bezoro/ahk-manager/releases',
        ])
          await assert.rejects(
            fetcher(url, { redirect: 'manual', signal: AbortSignal.timeout(3000) }),
          );
      },
    );
    await check(
      'the shipped 1.2.16 client reproduces Electron manual-redirect cancellation',
      async () => {
        const legacy = require(path.join(__dirname, 'legacy-client.cjs')) as {
          GitHubUpdateClient: new (fetcher: typeof fetch) => {
            releases(token: null): Promise<unknown>;
          };
        };
        const original = (async (input, init) => {
          try {
            return await network.fetch(input instanceof URL ? input.href : input, init);
          } catch (error) {
            legacyCause = error instanceof Error ? error.message : 'Unknown transport failure.';
            throw error;
          }
        }) as typeof fetch;
        await assert.rejects(new legacy.GitHubUpdateClient(original).releases(null));
        assert.equal(legacyCause, 'Redirect was cancelled');
      },
    );
    await check(
      'the production transport resolves the stable manifest through a real Chromium redirect',
      async () => {
        const result = await new GitHubUpdateClient(fetcher).releases('stable');
        assert.deepEqual(
          result.map((release) => release.version),
          ['1.3.1', '1.3.0'],
        );
        assert.equal(result[0]!.metadataOnly, undefined);
        assert.equal(result[0]!.sha256, stable.manifest.installer.sha256);
      },
    );
    await check(
      'test releases and lazy history resolve through the same protected session',
      async () => {
        const client = new GitHubUpdateClient(fetcher);
        const result = await client.releases('test');
        assert.deepEqual(
          result.map((release) => release.version),
          ['1.3.2-test.40.1.2', '1.3.1', '1.3.0'],
        );
        assert.equal(
          (await client.resolve('github:10')).sha256,
          previous.manifest.installer.sha256,
        );
        assert.equal((await client.releases('test'))[0]!.sha256, preview.manifest.installer.sha256);
      },
    );
    await check('a redirect outside the asset allowlist cannot become a manifest', async () => {
      mode = 'untrusted';
      try {
        await assert.rejects(new GitHubUpdateClient(fetcher).releases('stable'));
      } finally {
        mode = 'normal';
      }
    });
    await check('redirect loops stop within the existing client hop limit', async () => {
      const before = requests.length;
      mode = 'loop';
      try {
        await assert.rejects(new GitHubUpdateClient(fetcher).releases('stable'));
      } finally {
        mode = 'normal';
      }
      assert.ok(requests.length - before <= 5);
    });
    await check('oversized metadata is refused before manifest resolution', async () => {
      mode = 'oversized';
      try {
        await assert.rejects(new GitHubUpdateClient(fetcher).releases('stable'));
      } finally {
        mode = 'normal';
      }
    });
    await check('AbortSignal cancels a pending Chromium update request', async () => {
      mode = 'delayed';
      try {
        await assert.rejects(
          fetcher(API, { redirect: 'manual', signal: AbortSignal.timeout(30) }),
          { name: 'TimeoutError' },
        );
      } finally {
        mode = 'normal';
      }
      await new Promise<void>((resolve) => setTimeout(resolve, 300));
    });
    await check(
      'site credentials and seeded session cookies never reach update requests',
      async () => {
        assert.ok(requests.length > 5);
        assert.ok(requests.every((request) => !request.authorization && !request.cookie));
      },
    );

    network.protocol.unhandle('https');
    await network.closeAllConnections();
    clearTimeout(deadline);
    fs.writeFileSync(
      path.join(output, 'summary.json'),
      JSON.stringify(
        {
          fixture: true,
          electron: process.versions.electron,
          node: process.versions.node,
          legacyCommit,
          legacyCause,
          productionSessionPolicy: true,
          checks,
          failures,
          requests,
          credentialsProvided: false,
          installersDownloadedOrExecuted: false,
        },
        null,
        2,
      ),
    );
    console.log(
      JSON.stringify({
        passed: failures.length === 0,
        checks: checks.length,
        failures,
        legacyCause,
      }),
    );
    app.exit(failures.length ? 1 : 0);
  })
  .catch((error) => {
    clearTimeout(deadline);
    fs.writeFileSync(
      path.join(output, 'summary.json'),
      JSON.stringify({
        fixture: true,
        checks,
        failures: [error instanceof Error ? error.message : 'Harness startup failed.'],
      }),
    );
    app.exit(1);
  });
