import test from 'node:test';
import assert from 'node:assert/strict';
import {
  UpdateService,
  type UpdateBoundary,
  type UpdateStorePort,
} from '../../src/main/update-service.js';
import {
  compareVersions,
  type RemoteUpdateRelease,
} from '../../src/main/github-updates.js';
import type { CachedInstaller } from '../../src/main/update-store.js';
import type { UpdateChannel, UpdateState } from '../../src/shared/updates.js';

function deferred<T>(): {
  promise: Promise<T>;
  resolve(value: T): void;
  reject(error: Error): void;
} {
  let resolve!: (value: T) => void, reject!: (error: Error) => void;
  const promise = new Promise<T>((done, fail) => {
    resolve = done;
    reject = fail;
  });
  return { promise, resolve, reject };
}
const hash = 'a'.repeat(64);
const cached = (version: string): CachedInstaller => ({
  version,
  installerPath: `/fixture/${version}/installer.exe`,
  sha256: hash,
  bytes: 123,
});
const release = (version = '1.2.10', id = `github:${version}`): RemoteUpdateRelease => ({
  id,
  version,
  publishedAt: '2026-10-28T00:00:00Z',
  notes: `Fixture release ${version}`,
  commit: 'b'.repeat(40),
  assetId: 42,
  sha256: hash,
  sha512: Buffer.alloc(64, 7).toString('base64'),
  bytes: 123,
  installerName: `AHK-Manager-${version}-Setup-x64.exe`,
});
const metadata = (version = '1.2.0'): RemoteUpdateRelease => ({
  ...release(version),
  metadataOnly: true,
  sha256: '',
  sha512: '',
  commit: '',
});

class Store implements UpdateStorePort {
  items: CachedInstaller[] = [];
  corrupt = new Set<string>();
  cachedSources: string[] = [];
  channel: UpdateChannel = 'stable';
  channelSaveGate: Promise<void> | null = null;
  savedChannels: UpdateChannel[] = [];
  async readChannel(): Promise<UpdateChannel> {
    return this.channel;
  }
  async saveChannel(channel: UpdateChannel): Promise<void> {
    this.savedChannels.push(channel);
    if (this.channelSaveGate) await this.channelSaveGate;
    this.channel = channel;
  }
  cacheError: Error | null = null;
  cleanupCalls = 0;
  cleanupError: Error | null = null;
  async removeLegacyAccess(): Promise<void> {
    this.cleanupCalls++;
    if (this.cleanupError) throw this.cleanupError;
  }
  async cached(): Promise<CachedInstaller[]> {
    return structuredClone(this.items);
  }
  async verify(value: CachedInstaller): Promise<void> {
    if (this.corrupt.has(value.version)) throw new Error('Fixture cache integrity failure.');
  }
  async cache(
    version: string,
    source: string,
    sha256: string,
    bytes: number,
  ): Promise<CachedInstaller> {
    if (this.cacheError) throw this.cacheError;
    this.cachedSources.push(source);
    this.corrupt.delete(version);
    const value = { ...cached(version), sha256, bytes };
    this.items = [...this.items.filter((item) => item.version !== version), value];
    return value;
  }
}

function fixture(version = '1.2.2') {
  const store = new Store();
  const notifications: UpdateState[] = [];
  const queriedChannels: UpdateChannel[] = [];
  const resolved: Array<{ id: string }> = [];
  const downloaded: RemoteUpdateRelease[] = [];
  const applied: Array<{ operationId: string; target: CachedInstaller }> = [];
  const preflights: Array<{ operationId: string; phase: 'begin' | 'commit' }> = [];
  const confirmed: string[] = [];
  const hooks = {
    releases: async (_channel: UpdateChannel): Promise<RemoteUpdateRelease[]> => [release()],
    resolve: async (_id: string): Promise<RemoteUpdateRelease> => release('1.2.0'),
    download: async (
      _item: RemoteUpdateRelease,
      progress: (value: number) => void,
    ): Promise<string> => {
      progress(60);
      return '/fixture/downloaded.exe';
    },
    preflight: async (_operationId: string, _phase: 'begin' | 'commit'): Promise<boolean> => true,
    confirm: async (): Promise<boolean> => true,
    apply: async (): Promise<void> => {},
  };
  const boundary: UpdateBoundary = {
    version,
    store,
    releases: async (channel) => {
      queriedChannels.push(channel);
      return hooks.releases(channel);
    },
    resolve: async (id) => {
      resolved.push({ id });
      return hooks.resolve(id);
    },
    download: async (item, progress) => {
      downloaded.push(item);
      return hooks.download(item, progress);
    },
    preflight: async (operationId, phase) => {
      preflights.push({ operationId, phase });
      return hooks.preflight(operationId, phase);
    },
    confirm: async (item) => {
      confirmed.push(item.id);
      return hooks.confirm();
    },
    apply: async (operationId, target) => {
      applied.push({ operationId, target });
      await hooks.apply();
    },
    changed: (state) => {
      notifications.push(state);
    },
  };
  return {
    service: new UpdateService(boundary),
    boundary,
    store,
    hooks,
    notifications,
    queriedChannels,
    resolved,
    downloaded,
    applied,
    preflights,
    confirmed,
  };
}

test('update checks order numeric versions and expose local rollback history without downloading or applying', async () => {
  const f = fixture();
  f.store.items = [cached('1.2.0'), cached('1.1.99'), cached('1.2.2')];
  f.hooks.releases = async () => [release('1.2.9'), release('1.2.10'), release('1.2.2')];
  await f.service.initialize();
  const state = await f.service.check();
  assert.deepEqual(
    state.releases.map((item) => item.version),
    ['1.2.10', '1.2.9', '1.2.2', '1.2.0'],
  );
  assert.equal(state.availableVersion, '1.2.10');
  assert.equal(state.releases.find((item) => item.version === '1.2.0')?.canInstall, true);
  assert.equal(state.releases.find((item) => item.installed)?.canInstall, false);
  assert.equal(compareVersions('1.2.10', '1.2.9'), 1);
  assert.deepEqual(f.downloaded, []);
  assert.equal(f.applied.length, 0);
  assert.deepEqual(f.preflights, []);
  const detached = f.service.snapshot;
  detached.releases.length = 0;
  assert.equal(f.service.snapshot.releases.length, 4);
});

test('begin preflight refuses a dirty or busy host before confirmation, download or apply', async () => {
  const f = fixture();
  await f.service.check();
  f.hooks.preflight = async () => false;
  const state = await f.service.install(release().id);
  assert.equal(state.phase, 'error');
  assert.match(state.error!, /Save or discard/);
  assert.deepEqual(
    f.preflights.map((item) => item.phase),
    ['begin'],
  );
  assert.deepEqual(f.confirmed, []);
  assert.deepEqual(f.downloaded, []);
  assert.equal(f.applied.length, 0);
});

test('cancelled installation confirmation has no download, cache mutation or apply', async () => {
  const f = fixture();
  await f.service.check();
  f.hooks.confirm = async () => false;
  const state = await f.service.install(release().id);
  assert.equal(state.phase, 'idle');
  assert.equal(f.service.busy, false);
  assert.equal(f.confirmed.length, 1);
  assert.deepEqual(f.downloaded, []);
  assert.deepEqual(f.store.cachedSources, []);
  assert.equal(f.applied.length, 0);
});

test('duplicate install and background check cannot enter while begin preflight is awaiting', async () => {
  const f = fixture();
  await f.service.check();
  const gate = deferred<boolean>();
  f.hooks.preflight = async (_id, phase) => (phase === 'begin' ? gate.promise : true);
  const first = f.service.install(release().id);
  assert.equal(f.service.busy, true);
  await f.service.install(release().id);
  await f.service.check();
  assert.equal(f.preflights.length, 1);
  assert.equal(f.queriedChannels.length, 1);
  assert.deepEqual(f.downloaded, []);
  gate.resolve(false);
  await first;
  assert.equal(f.service.busy, false);
  assert.equal(f.applied.length, 0);
});

test('an active update check suppresses duplicate checks, channel changes and installation effects', async () => {
  const f = fixture();
  f.store.items = [cached('1.2.0')];
  await f.service.initialize();
  const gate = deferred<RemoteUpdateRelease[]>();
  f.hooks.releases = async () => gate.promise;
  const first = f.service.check();
  await Promise.resolve();
  await Promise.resolve();
  assert.equal(f.service.snapshot.phase, 'checking');
  await f.service.check();
  await f.service.setChannel('test');
  await f.service.install('local:1.2.0');
  assert.equal(f.queriedChannels.length, 1);
  assert.equal(f.applied.length, 0);
  gate.resolve([release()]);
  await first;
  assert.equal(f.service.snapshot.phase, 'idle');
});

test('commit preflight refusal preserves the verified download and a later explicit retry reuses it', async () => {
  const f = fixture();
  await f.service.check();
  f.hooks.preflight = async (_id, phase) => phase === 'begin';
  const refused = await f.service.install(release().id);
  assert.equal(refused.phase, 'error');
  assert.equal(refused.progress, null);
  assert.match(refused.error!, /preserved/);
  assert.equal(f.store.items.length, 1);
  assert.equal(f.downloaded.length, 1);
  assert.equal(f.applied.length, 0);
  assert.deepEqual(
    f.preflights.map((item) => item.phase),
    ['begin', 'commit'],
  );
  assert.equal(f.preflights[0]!.operationId, f.preflights[1]!.operationId);
  f.hooks.preflight = async () => true;
  await f.service.install(release().id);
  assert.equal(f.downloaded.length, 1);
  assert.equal(f.applied.length, 1);
  assert.notEqual(f.applied[0]!.operationId, f.preflights[0]!.operationId);
});

test('corrupt cached installer is downloaded again when the matching remote release remains available', async () => {
  const f = fixture();
  f.store.items = [cached('1.2.10')];
  f.store.corrupt.add('1.2.10');
  await f.service.check();
  await f.service.install(release().id);
  assert.equal(f.downloaded.length, 1);
  assert.equal(f.store.cachedSources.length, 1);
  assert.equal(f.applied.length, 1);
  assert.equal(f.store.corrupt.size, 0);
});

test('a same-version cached receipt with a different hash is replaced only from the selected remote release', async () => {
  const f = fixture();
  f.store.items = [{ ...cached('1.2.10'), sha256: 'c'.repeat(64) }];
  const state = await f.service.check();
  assert.equal(state.releases.find((item) => item.id === release().id)!.cached, false);
  await f.service.install(release().id);
  assert.equal(f.downloaded.length, 1);
  assert.equal(f.downloaded[0]!.id, release().id);
  assert.equal(f.applied.length, 1);
  assert.equal(f.applied[0]!.target.sha256, hash);
});

test('offline corrupt cache blocks apply and never invents a download source or repeats an effect', async () => {
  const f = fixture();
  f.store.items = [cached('1.2.0')];
  f.store.corrupt.add('1.2.0');
  await f.service.initialize();
  f.hooks.releases = async () => {
    throw new Error('Fixture offline.');
  };
  await f.service.check();
  const state = await f.service.install('local:1.2.0');
  assert.equal(state.phase, 'error');
  assert.match(state.error!, /integrity/);
  assert.deepEqual(f.downloaded, []);
  assert.equal(f.applied.length, 0);
  assert.equal(f.service.busy, false);
});

test('download and cache-integrity failures cannot reach commit preflight or apply', async () => {
  for (const at of ['download', 'cache'] as const) {
    const f = fixture();
    await f.service.check();
    if (at === 'download')
      f.hooks.download = async () => {
        throw new Error('Fixture download interrupted.');
      };
    else f.store.cacheError = new Error('Fixture downloaded bytes do not match.');
    const state = await f.service.install(release().id);
    assert.equal(state.phase, 'error');
    assert.equal(state.progress, null);
    assert.equal(f.applied.length, 0);
    assert.deepEqual(
      f.preflights.map((item) => item.phase),
      ['begin'],
    );
    assert.equal(f.service.busy, false);
  }
});

test('private access details are absent from snapshots when a remote or download error contains credentials or URLs', async () => {
  const f = fixture();
  f.hooks.releases = async () => {
    throw new Error('GET https://api.github.com/private?token=ghp_fixture_secret_123456789012345');
  };
  const check = await f.service.check();
  assert.equal(check.phase, 'error');
  assert.doesNotMatch(JSON.stringify(check), /ghp_fixture|https:/);
  f.hooks.releases = async () => [release()];
  await f.service.check();
  f.hooks.download = async () => {
    throw new Error('github_pat_fixture_secret_123456789012345');
  };
  const install = await f.service.install(release().id);
  assert.equal(install.phase, 'error');
  assert.doesNotMatch(JSON.stringify(install), /github_pat_fixture/);
  assert.equal(f.applied.length, 0);
});

test('apply uses one operation after final verification and rejects concurrent installation while awaiting shutdown', async () => {
  const f = fixture();
  await f.service.check();
  const gate = deferred<void>();
  const entered = deferred<void>();
  f.hooks.apply = async () => {
    entered.resolve(undefined);
    await gate.promise;
  };
  const first = f.service.install(release().id);
  await entered.promise;
  assert.equal(f.service.snapshot.phase, 'installing');
  assert.equal(f.service.busy, true);
  await f.service.install(release().id);
  await f.service.check();
  assert.equal(f.applied.length, 1);
  assert.equal(f.queriedChannels.length, 1);
  assert.equal(
    f.applied[0]!.operationId,
    f.preflights.find((item) => item.phase === 'commit')!.operationId,
  );
  gate.resolve(undefined);
  await first;
});

test('apply failure leaves recovery state and the cached installer without automatically retrying', async () => {
  const f = fixture();
  await f.service.check();
  f.hooks.apply = async () => {
    throw new Error('Fixture shutdown not confirmed.');
  };
  const state = await f.service.install(release().id);
  assert.equal(state.phase, 'error');
  assert.match(state.error!, /shutdown/);
  assert.equal(state.progress, null);
  assert.equal(f.applied.length, 1);
  assert.equal(f.downloaded.length, 1);
  assert.equal(f.store.items.length, 1);
  assert.equal(f.service.busy, false);
});

test('unavailable history identity cannot download or install a different release', async () => {
  const f = fixture();
  await f.service.check();
  const state = await f.service.install('github:removed-or-forged');
  assert.equal(state.phase, 'error');
  assert.match(state.error!, /no longer available/);
  assert.deepEqual(f.preflights, []);
  assert.deepEqual(f.downloaded, []);
  assert.equal(f.applied.length, 0);
});

test('public history checking leaves older manifests lazy and never resolves or downloads them automatically', async () => {
  const f = fixture();
  f.hooks.releases = async () => [release(), metadata('1.2.0'), metadata('1.2.1')];
  const state = await f.service.check();
  assert.deepEqual(
    state.releases.map((item) => item.version),
    ['1.2.10', '1.2.2', '1.2.1', '1.2.0'],
  );
  assert.equal(state.availableVersion, '1.2.10');
  assert.deepEqual(f.queriedChannels, ['stable']);
  assert.deepEqual(f.resolved, []);
  assert.deepEqual(f.downloaded, []);
  assert.equal(f.applied.length, 0);
});

test('explicitly selecting an older metadata-only release resolves that exact version before download and commit', async () => {
  const f = fixture();
  f.hooks.releases = async () => [release(), metadata()];
  await f.service.check();
  const state = await f.service.install(metadata().id);
  assert.deepEqual(f.resolved, [{ id: metadata().id }]);
  assert.equal(f.downloaded.length, 1);
  assert.deepEqual(f.downloaded[0], release('1.2.0'));
  assert.equal(f.applied.length, 1);
  assert.equal(f.applied[0]!.target.version, '1.2.0');
  assert.equal(f.applied[0]!.target.sha256, hash);
  assert.equal(state.phase, 'installing');
  assert.deepEqual(
    f.preflights.map((item) => item.phase),
    ['begin', 'commit'],
  );
  assert.equal(f.preflights[0]!.operationId, f.preflights[1]!.operationId);
});

test('missing lazy resolver or a failed manifest request cannot reach download, cache or apply', async () => {
  for (const missing of [true, false]) {
    const f = fixture();
    f.hooks.releases = async () => [metadata()];
    if (missing) delete f.boundary.resolve;
    else
      f.hooks.resolve = async () => {
        throw new Error('Fixture manifest unavailable.');
      };
    await f.service.check();
    const state = await f.service.install(metadata().id);
    assert.equal(state.phase, 'error');
    assert.equal(state.progress, null);
    assert.equal(f.service.busy, false);
    assert.equal(f.resolved.length, missing ? 0 : 1);
    assert.deepEqual(f.downloaded, []);
    assert.deepEqual(f.store.cachedSources, []);
    assert.equal(f.applied.length, 0);
    assert.deepEqual(
      f.preflights.map((item) => item.phase),
      ['begin'],
    );
  }
});

test('resolved manifest must retain release id, version, asset id, byte count and complete metadata', async () => {
  const complete = release('1.2.0');
  for (const changed of [
    { ...complete, id: 'github:another-release' },
    { ...complete, version: '1.2.1' },
    { ...complete, assetId: complete.assetId + 1 },
    { ...complete, bytes: complete.bytes + 1 },
    { ...complete, metadataOnly: true },
  ]) {
    const f = fixture();
    f.hooks.releases = async () => [metadata()];
    f.hooks.resolve = async () => changed;
    await f.service.check();
    const state = await f.service.install(metadata().id);
    assert.equal(state.phase, 'error');
    assert.match(state.error!, /identity/);
    assert.deepEqual(f.downloaded, []);
    assert.deepEqual(f.store.cachedSources, []);
    assert.equal(f.applied.length, 0);
    assert.deepEqual(
      f.preflights.map((item) => item.phase),
      ['begin'],
    );
  }
});

test('a verified complete local installer stays usable offline without requesting a remote manifest', async () => {
  const f = fixture();
  f.store.items = [cached('1.2.0')];
  f.hooks.releases = async () => {
    throw new Error('Fixture offline.');
  };
  f.hooks.resolve = async () => {
    throw new Error('A cached local choice must not contact the API.');
  };
  await f.service.initialize();
  await f.service.check();
  const state = await f.service.install('local:1.2.0');
  assert.equal(state.phase, 'installing');
  assert.deepEqual(f.resolved, []);
  assert.deepEqual(f.downloaded, []);
  assert.equal(f.applied.length, 1);
  assert.deepEqual(f.applied[0]!.target, cached('1.2.0'));
});

test('metadata-only GitHub history preserves a complete cached rollback even when the next network check fails', async () => {
  const f = fixture();
  f.store.items = [cached('1.2.0')];
  f.hooks.releases = async () => [metadata()];
  f.hooks.resolve = async () => {
    throw new Error('Fixture offline after history was listed.');
  };
  await f.service.initialize();
  const listed = await f.service.check();
  assert.equal(listed.releases.find((item) => item.id === metadata().id)?.cached, true);
  f.hooks.releases = async () => {
    throw new Error('Fixture offline.');
  };
  assert.equal((await f.service.check()).phase, 'error');
  const state = await f.service.install(metadata().id);
  assert.equal(state.phase, 'installing');
  assert.deepEqual(f.resolved, []);
  assert.deepEqual(f.downloaded, []);
  assert.equal(f.applied.length, 1);
  assert.deepEqual(f.applied[0]!.target, cached('1.2.0'));
});

test('a corrupt installer behind metadata-only history resolves its manifest and downloads a verified replacement', async () => {
  const f = fixture();
  f.store.items = [cached('1.2.0')];
  f.store.corrupt.add('1.2.0');
  f.hooks.releases = async () => [metadata()];
  await f.service.check();
  const state = await f.service.install(metadata().id);
  assert.equal(state.phase, 'installing');
  assert.deepEqual(f.resolved, [{ id: metadata().id }]);
  assert.equal(f.downloaded.length, 1);
  assert.deepEqual(f.downloaded[0], release('1.2.0'));
  assert.equal(f.store.cachedSources.length, 1);
  assert.equal(f.store.corrupt.has('1.2.0'), false);
  assert.equal(f.applied.length, 1);
  assert.equal(f.applied[0]!.target.sha256, hash);
});

test('a corrupt cached historical installer cannot bypass unavailable lazy metadata while offline', async () => {
  const f = fixture();
  f.store.items = [cached('1.2.0')];
  f.store.corrupt.add('1.2.0');
  f.hooks.releases = async () => [metadata()];
  f.hooks.resolve = async () => {
    throw new Error('Fixture historical manifest offline.');
  };
  await f.service.check();
  const state = await f.service.install(metadata().id);
  assert.equal(state.phase, 'error');
  assert.equal(f.service.busy, false);
  assert.equal(f.resolved.length, 1);
  assert.deepEqual(f.downloaded, []);
  assert.deepEqual(f.store.cachedSources, []);
  assert.equal(f.applied.length, 0);
  assert.equal(f.store.corrupt.has('1.2.0'), true);
});

test('a pending lazy manifest resolution owns the installation operation and cannot be duplicated or replaced by a check', async () => {
  const f = fixture();
  f.hooks.releases = async () => [metadata()];
  await f.service.check();
  const gate = deferred<RemoteUpdateRelease[]>();
  const entered = deferred<void>();
  f.hooks.resolve = async () => {
    entered.resolve(undefined);
    return (await gate.promise)[0]!;
  };
  const first = f.service.install(metadata().id);
  await entered.promise;
  assert.equal(f.service.busy, true);
  assert.equal(f.service.snapshot.phase, 'downloading');
  await f.service.install(metadata().id);
  await f.service.check();
  assert.equal(f.resolved.length, 1);
  assert.equal(f.queriedChannels.length, 1);
  assert.deepEqual(f.downloaded, []);
  assert.equal(f.applied.length, 0);
  gate.resolve([release('1.2.0')]);
  await first;
  assert.equal(f.resolved.length, 1);
  assert.equal(f.downloaded.length, 1);
  assert.equal(f.applied.length, 1);
});

test('stable defaults exclude test releases from both remote and preserved installer history', async () => {
  const f = fixture();
  const preview = '1.3.0-test.2.10.1';
  f.store.items = [cached(preview), cached('1.2.0')];
  f.hooks.releases = async () => [release(preview), release()];
  await f.service.initialize();
  const state = await f.service.check();
  assert.equal(state.channel, 'stable');
  assert.equal(state.availableVersion, '1.2.10');
  assert.equal(
    state.releases.some((item) => item.version === preview),
    false,
  );
  await f.service.install(`local:${preview}`);
  await f.service.install(release(preview).id);
  assert.deepEqual(f.applied, []);
  assert.deepEqual(f.downloaded, []);
  assert.deepEqual(f.queriedChannels, ['stable']);
});

test('persisted test channel survives initialization and explicitly queries previews', async () => {
  const f = fixture();
  f.store.channel = 'test';
  f.hooks.releases = async (channel) =>
    channel === 'test' ? [release('1.3.0-test.2.10.1')] : [release()];
  assert.equal((await f.service.initialize()).channel, 'test');
  assert.equal((await f.service.check()).availableVersion, '1.3.0-test.2.10.1');
  assert.deepEqual(f.queriedChannels, ['test']);
  assert.deepEqual(f.downloaded, []);
});

test('switching to stable offers the newest stable even below the installed test version and blocks all other previews', async () => {
  const current = '1.3.0-test.2.9.1',
    other = '1.3.0-test.2.10.1';
  const f = fixture(current);
  f.store.channel = 'test';
  f.store.items = [cached(current), cached(other), cached('1.2.15')];
  f.hooks.releases = async () => [release(other), release('1.2.16'), release('1.2.15')];
  await f.service.initialize();
  assert.equal((await f.service.check()).availableVersion, other);
  const stable = await f.service.setChannel('stable');
  assert.equal(stable.channel, 'stable');
  assert.equal(stable.availableVersion, '1.2.16');
  assert.deepEqual(
    stable.releases.map((item) => item.version),
    [current, '1.2.16', '1.2.15'],
  );
  assert.equal(stable.releases.find((item) => item.version === current)?.canInstall, false);
  await f.service.install(release(other).id);
  assert.equal(f.applied.length, 0);
  await f.service.install(release('1.2.16').id);
  assert.equal(f.applied[0]?.target.version, '1.2.16');
  assert.deepEqual(f.queriedChannels, ['test', 'stable']);
});

test('channel persistence owns the operation before awaiting and clears former remote choices immediately', async () => {
  const f = fixture();
  await f.service.check();
  const gate = deferred<void>();
  f.store.channelSaveGate = gate.promise;
  const switching = f.service.setChannel('test');
  assert.equal(f.service.busy, true);
  assert.equal(f.service.snapshot.phase, 'checking');
  assert.equal(
    f.service.snapshot.releases.some((item) => item.id === release().id),
    false,
  );
  await f.service.setChannel('stable');
  await f.service.check();
  await f.service.install(release().id);
  assert.deepEqual(f.store.savedChannels, ['test']);
  assert.deepEqual(f.applied, []);
  assert.deepEqual(f.queriedChannels, ['stable']);
  gate.resolve(undefined);
  const state = await switching;
  assert.equal(state.channel, 'test');
  assert.deepEqual(f.queriedChannels, ['stable', 'test']);
});

test('failed channel persistence retains the previous channel and cannot query an unsaved selection', async () => {
  const f = fixture();
  await f.service.check();
  const gate = deferred<void>();
  f.store.channelSaveGate = gate.promise;
  const switching = f.service.setChannel('test');
  gate.reject(new Error('Fixture settings write failed.'));
  const failed = await switching;
  assert.equal(failed.channel, 'stable');
  assert.equal(failed.phase, 'error');
  assert.match(failed.error!, /previous channel was kept/);
  assert.equal(f.service.busy, false);
  assert.deepEqual(f.queriedChannels, ['stable']);
  f.store.channelSaveGate = null;
  const retried = await f.service.setChannel('test');
  assert.equal(retried.channel, 'test');
  assert.deepEqual(f.queriedChannels, ['stable', 'test']);
});

test('a pending check or installation rejects channel changes without changing persistence or selection', async () => {
  const f = fixture();
  const gate = deferred<RemoteUpdateRelease[]>();
  f.hooks.releases = async () => gate.promise;
  const checking = f.service.check();
  await f.service.setChannel('test');
  assert.deepEqual(f.store.savedChannels, []);
  gate.resolve([release()]);
  await checking;
  const applyGate = deferred<void>(),
    entered = deferred<void>();
  f.hooks.apply = async () => {
    entered.resolve(undefined);
    await applyGate.promise;
  };
  const installing = f.service.install(release().id);
  await entered.promise;
  await f.service.setChannel('test');
  assert.equal(f.service.snapshot.channel, 'stable');
  assert.deepEqual(f.store.savedChannels, []);
  applyGate.resolve(undefined);
  await installing;
});

test('a new channel network failure cannot restore or install a release id from the previous channel', async () => {
  const f = fixture();
  f.store.channel = 'test';
  f.hooks.releases = async () => [release('1.3.0-test.2.10.1')];
  await f.service.initialize();
  await f.service.check();
  f.hooks.releases = async () => {
    throw new Error('Fixture offline.');
  };
  const state = await f.service.setChannel('stable');
  assert.equal(state.channel, 'stable');
  assert.equal(state.phase, 'error');
  assert.equal(
    state.releases.some((item) => item.version === '1.3.0-test.2.10.1'),
    false,
  );
  await f.service.install(release('1.3.0-test.2.10.1').id);
  assert.deepEqual(f.applied, []);
  assert.deepEqual(f.downloaded, []);
});

test('explicit test opt-in offers a published preview even below the installed stable bootstrap', async () => {
  const f = fixture('1.3.0');
  const preview = '1.3.0-test.18.1.2';
  f.store.items = [cached('1.4.0-test.17.1.9')];
  f.hooks.releases = async () => [release('1.3.0'), release(preview)];
  const state = await f.service.setChannel('test');
  assert.equal(
    state.availableVersion,
    preview,
    'Published preview takes precedence over cached historical choices.',
  );
  assert.equal(state.channel, 'test');
  assert.equal(state.releases.find((item) => item.version === preview)?.canInstall, true);
  await f.service.install(release(preview).id);
  assert.equal(f.applied[0]?.target.version, preview);
});

test('offline test opt-in can offer its preserved preview while stable never offers that cache', async () => {
  const f = fixture('1.3.0');
  const preview = '1.3.0-test.18.1.2';
  f.store.items = [cached(preview)];
  f.hooks.releases = async () => {
    throw new Error('Fixture offline.');
  };
  await f.service.initialize();
  assert.equal(f.service.snapshot.availableVersion, null);
  assert.equal((await f.service.setChannel('test')).availableVersion, preview);
  await f.service.install(`local:${preview}`);
  assert.equal(f.applied[0]?.target.version, preview);
  assert.deepEqual(f.downloaded, []);
});

test('a persisted test channel never cycles back into the old preview after accepting its stable release', async () => {
  const preview = '1.3.0-test.18.1.2';
  const before = fixture(preview);
  before.store.channel = 'test';
  before.hooks.releases = async () => [release(preview), release('1.3.0')];
  await before.service.initialize();
  assert.equal((await before.service.check()).availableVersion, '1.3.0');
  await before.service.install(release('1.3.0').id);
  assert.equal(before.applied[0]?.target.version, '1.3.0');
  const restarted = fixture('1.3.0');
  restarted.store.channel = 'test';
  restarted.store.items = [cached(preview)];
  restarted.hooks.releases = async () => [release(preview), release('1.3.0')];
  await restarted.service.initialize();
  const current = await restarted.service.check();
  assert.equal(current.channel, 'test');
  assert.equal(current.availableVersion, null);
  assert.equal(
    current.releases.find((item) => item.version === preview)?.canInstall,
    true,
    'Explicit history rollback remains available.',
  );
  await restarted.service.setChannel('stable');
  assert.equal(
    (await restarted.service.setChannel('test')).availableVersion,
    preview,
    'An explicit new opt-in can still choose the older bootstrap preview.',
  );
});

test('retired credential cleanup cannot block public initialization or update checks', async () => {
  const f = fixture();
  f.store.cleanupError = new Error('Fixture legacy credential cleanup was denied.');
  const initial = await f.service.initialize();
  assert.equal(initial.phase, 'idle');
  assert.equal(initial.error, null);
  assert.equal(f.store.cleanupCalls, 1);
  const checked = await f.service.check();
  assert.equal(checked.error, null);
  assert.equal(checked.availableVersion, '1.2.10');
  assert.deepEqual(f.queriedChannels, ['stable']);
});
