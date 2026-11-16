import assert from 'node:assert/strict';
import { createHash, randomUUID } from 'node:crypto';
import { EventEmitter } from 'node:events';
import fsSync from 'node:fs';
import fs from 'node:fs/promises';
import { createRequire } from 'node:module';
import path from 'node:path';
import test, { type TestContext } from 'node:test';
import type { AgentReply, AgentRequest } from '../../src/main/agent-transport.js';
import { createCommandLog } from '../../src/main/logging/create-command-log.js';
import type { CommandLog, CommandLogSnapshot } from '../../src/main/logging/command-log.js';
import { resolveDeployment } from '../../src/main/storage/deployment.js';
import { prepareDataMove } from '../../src/main/storage/data-migration.js';
import { NativeStorageLeaseError } from '../../src/main/storage/native-storage-lease.js';
import { UpdateStore } from '../../src/main/updates/update-store.js';
import { UpdateService, type UpdateBoundary } from '../../src/main/updates/update-service.js';
import type { RemoteUpdateRelease } from '../../src/main/updates/github-updates.js';
import { hostText as tText } from '../../src/shared/host-en-US.js';
import { SHUTDOWN_DEADLINE_MS } from '../../src/main/lifecycle.js';
import { planId, runningSnapshot } from './fixtures.js';

const requireFixture = createRequire(path.join(process.cwd(), 'package.json'));
const bridge = Symbol.for('ahk-manager-agent-fixture');
const dispatchTimeout = 1_000;

async function waitFor<T>(read: () => T | undefined | Promise<T | undefined>): Promise<T> {
  const until = Date.now() + dispatchTimeout;
  while (Date.now() < until) {
    const value = await read();
    if (value !== undefined) return value;
    await new Promise((resolve) => setTimeout(resolve, 5));
  }
  throw new Error('The fixture did not reach the expected state.');
}

interface FixturePorts {
  savePlacement?(): Promise<void>;
  scheduleShutdown?(callback: () => void, milliseconds: number): () => void;
  captureCommands?: boolean;
  dataMoveFailure?: Error;
  createUpdates?(
    boundary: Pick<UpdateBoundary, 'preflight' | 'confirm' | 'changed'>,
    store: UpdateStore,
  ): Promise<UpdateService>;
}

async function fixture(t: TestContext, busyLogonMarker = false, ports: FixturePorts = {}) {
  const staging = process.env.AHK_DIAGNOSTICS_TEST_ROOT;
  assert.ok(staging && path.isAbsolute(staging));
  const root = await fs.mkdtemp(path.join(staging, 'agent-application-'));
  let commandLog: CommandLog | null = null;
  t.after(async () => {
    commandLog?.close();
    await fs.rm(root, { recursive: true, force: true });
  });
  const executable = path.join(root, 'App', 'Bezoro.AhkManager.exe');
  const local = path.join(root, 'Local');
  await fs.mkdir(path.dirname(executable));
  await fs.mkdir(local);
  await fs.writeFile(path.join(root, 'package.json'), '{}');
  await fs.writeFile(
    path.join(root, 'portable.json'),
    JSON.stringify({
      schemaVersion: 1,
      applicationId: 'Bezoro.AhkManager.Electron',
      dataDirectory: 'Data',
    }),
  );
  const layout = resolveDeployment(executable, local);
  await fs.mkdir(layout.profileDirectory, { recursive: true });
  await fs.mkdir(layout.updatesDirectory);
  if (ports.dataMoveFailure) {
    await fs.writeFile(path.join(layout.profileDirectory, 'settings.json'), '{"preserved":true}');
    await prepareDataMove(layout, path.join(root, 'Moved'), async (directory) => {
      await fs.mkdir(directory);
    });
  }
  const events: string[] = [];
  const exitLogs: CommandLogSnapshot[] = [];
  let storageFailure: ((error: Error) => void) | undefined;
  let menu: Array<{ label?: string; click?: () => void }> = [];
  let dispatch: ((request: AgentRequest, alive: () => boolean) => Promise<AgentReply>) | undefined;
  let state = runningSnapshot();
  const done = { outcome: 'Confirmed', code: 'None', detail: null, correlationId: null };
  class Backend extends EventEmitter {
    authorityMode = 'Administrator';
    ready = false;
    exited = false;
    start() {
      this.ready = true;
    }
    async shutdown() {
      this.ready = false;
      this.exited = true;
      events.push('backend.shutdown');
    }
    async request(action: string, data: { selection?: unknown; confirmed?: boolean }) {
      if (action === 'refresh' || action === 'load') return structuredClone(state);
      if (action === 'saveSettings') await ports.savePlacement?.();
      if (action === 'prepare') {
        const script = state.scripts[0]!;
        return {
          planId,
          plan: {
            operation: 'Stop',
            scriptId: script.id,
            stateRevision: state.revision,
            expectedIdentity: script.process?.identity,
            selection: data.selection,
            allowed: true,
            requiresConfirmation: true,
            reason: 'None',
          },
        };
      }
      if (action === 'perform' && data.confirmed) {
        events.push('script.stop');
        const script = state.scripts[0]!;
        state = {
          ...state,
          runningCount: 0,
          scripts: [{ ...script, status: 'Stopped', process: null }],
        };
      }
      return done;
    }
  }
  class Window extends EventEmitter {
    readonly webContents = Object.assign(new EventEmitter(), { setWindowOpenHandler() {} });
    private visible = false;
    isDestroyed() {
      return false;
    }
    isVisible() {
      return this.visible;
    }
    isMinimized() {
      return false;
    }
    isMaximized() {
      return false;
    }
    setBounds() {}
    getBounds() {
      return state.settings.window;
    }
    async loadURL() {}
    show() {
      this.visible = true;
      events.push('window.show');
    }
    focus() {}
  }
  class Tray extends EventEmitter {
    setToolTip() {}
    isDestroyed() {
      return false;
    }
    destroy() {}
    setContextMenu(value: typeof menu) {
      menu = value;
    }
  }
  const app = Object.assign(new EventEmitter(), {
    isPackaged: true,
    commandLine: { appendSwitch() {} },
    setName() {},
    setAppUserModelId() {},
    getVersion: () => '1.2.1',
    getAppPath: () => root,
    getPath: (name: string) => (name === 'exe' ? executable : local),
    setPath(name: string, directory: string) {
      if (busyLogonMarker) events.push(name + '=' + directory);
    },
    requestSingleInstanceLock: () => {
      if (busyLogonMarker) events.push('instance.lock');
      return true;
    },
    whenReady: async () => {},
    quit: () => {
      events.push('app.quit');
    },
    exit: (code: number) => {
      if (commandLog) exitLogs.push(commandLog.snapshot('errors'));
      events.push('app.exit=' + code);
    },
    relaunch: () => {
      events.push('app.relaunch');
    },
  });
  const store = new UpdateStore(layout.updatesDirectory);
  const mocks = {
    fixturePorts: ports,
    electron: {
      app,
      Tray,
      ipcMain: { handle() {}, on() {} },
      clipboard: { writeText() {} },
      dialog: {
        showMessageBox: async (_window: Window, options: { type: string }) => {
          events.push('dialog.' + options.type);
          return { response: 1 };
        },
        showErrorBox: (_title: string, detail: string) => {
          events.push('error.' + detail);
        },
      },
      protocol: { registerSchemesAsPrivileged() {} },
      Menu: { setApplicationMenu() {}, buildFromTemplate: (items: typeof menu) => items },
      nativeImage: { createFromPath: () => ({ isEmpty: () => false }) },
      nativeTheme: new EventEmitter(),
      systemPreferences: new EventEmitter(),
      screen: {
        getAllDisplays: () => [],
        screenToDipRect: (_window: Window, bounds: unknown) => bounds,
        dipToScreenRect: (_window: Window, bounds: unknown) => bounds,
        getDisplayMatching: () => ({ scaleFactor: 1 }),
      },
      shell: {},
    },
    './backend.js': { BackendClient: Backend },
    './agent-transport.js': {
      startAgentTransport: (
        _file: string,
        _host: unknown,
        delegate: NonNullable<typeof dispatch>,
      ) => {
        dispatch = delegate;
        return async () => {
          events.push('agent.close');
        };
      },
    },
    './desktop/assets.js': { serveAssets: async () => {}, lockSession() {} },
    './desktop/main-window.js': {
      createMainWindow: () => {
        events.push('window.create');
        return new Window();
      },
    },
    './desktop/window-diagnostics.js': { createWindowDiagnostics: () => ({ initialReady() {} }) },
    './desktop/theme.js': {
      DesktopTheme: class {
        update() {}
        initialize() {}
      },
    },
    './desktop/storage-bootstrap.js': {
      initializePortableStorage() {
        if (busyLogonMarker) events.push('storage.initialize');
      },
    },
    './desktop/portable-search-shortcut.js': { ensurePortableSearchShortcut: async () => {} },
    './storage/portable-storage-lease.js': {
      acquirePortableStorageLease: async (_layout: unknown, onFailure: (error: Error) => void) => {
        storageFailure = onFailure;
        return { close: async () => {} };
      },
    },
    './storage/data-move-lease.js': {
      withDataMoveLease: async (_layout: unknown, work: () => Promise<unknown>) => {
        if (ports.dataMoveFailure) throw ports.dataMoveFailure;
        return work();
      },
    },
    './logging/create-command-log.js': {
      createCommandLog: () => {
        if (ports.captureCommands) commandLog = createCommandLog(layout);
        return commandLog;
      },
    },
    './logging/startup-failure.js': {
      recordStartupFailure: ({ error }: { error: unknown }) => {
        events.push('startup.error.' + String(error));
        return {};
      },
      startupFailureDirectory: () => root,
    },
    './updates/application-updates.js': {
      createApplicationUpdates: async (
        boundary: Pick<UpdateBoundary, 'preflight' | 'confirm' | 'changed'>,
      ) => ({
        service: ports.createUpdates
          ? await ports.createUpdates(boundary, store)
          : { busy: false, snapshot: {} },
        store,
      }),
    },
  };
  const esbuild = requireFixture('esbuild') as typeof import('esbuild');
  const bundle = path.join(root, 'application.cjs');
  await esbuild.build({
    entryPoints: ['src/main/application.ts'],
    platform: 'node',
    format: 'cjs',
    bundle: true,
    outfile: bundle,
    plugins: [
      {
        name: 'headless-test-ports',
        setup(build) {
          // Inject only the shutdown operation's timer, retaining the real lifecycle
          // implementation and all unrelated startup, polling and receipt timers.
          if (ports.scheduleShutdown) {
            build.onResolve({ filter: /^\.\/lifecycle\.js$/ }, (args) =>
              args.importer === path.resolve('src/main/application.ts')
                ? { path: path.resolve('src/main/lifecycle.ts'), namespace: 'shutdown-clock' }
                : undefined,
            );
            build.onLoad({ filter: /.*/, namespace: 'shutdown-clock' }, ({ path: source }) => ({
              contents: `
                export * from ${JSON.stringify(source)};
                import { shutdownWithinDeadline as actual } from ${JSON.stringify(source)};
                export function shutdownWithinDeadline(boundary, milliseconds, options) {
                  const ports = globalThis[Symbol.for(${JSON.stringify(Symbol.keyFor(bridge))})].fixturePorts;
                  return actual(boundary, milliseconds, { ...options, schedule: ports.scheduleShutdown });
                }
              `,
              resolveDir: path.dirname(source),
            }));
          }
          // Only the host composition sees Windows in this isolated fixture.
          // Filesystem adapters keep the real OS semantics for temporary paths;
          // the shipped source and its Windows-only admission guard are unchanged.
          build.onLoad({ filter: /[/\\]application\.ts$/ }, async (args) => {
            if (args.path !== path.resolve('src/main/application.ts')) return;
            const compiled = await esbuild.transform(await fs.readFile(args.path, 'utf8'), {
              loader: 'ts',
              define: { 'process.platform': '"win32"' },
            });
            return { contents: compiled.code, loader: 'js', resolveDir: path.dirname(args.path) };
          });
          build.onResolve({ filter: /.*/ }, (args) =>
            (args.path === 'electron' || path.basename(args.importer) === 'application.ts') &&
            Object.hasOwn(mocks, args.path)
              ? { path: args.path, namespace: 'headless-test' }
              : undefined,
          );
          build.onLoad({ filter: /.*/, namespace: 'headless-test' }, ({ path }) => ({
            contents:
              'module.exports = globalThis[Symbol.for(' +
              JSON.stringify(Symbol.keyFor(bridge)) +
              ')][' +
              JSON.stringify(path) +
              ']',
          }));
        },
      },
    ],
  });
  const argv = process.argv;
  const resources = process.resourcesPath;
  const listeners = process.listeners('uncaughtExceptionMonitor');
  t.after(() => {
    for (const listener of process.listeners('uncaughtExceptionMonitor'))
      if (!listeners.includes(listener))
        process.removeListener('uncaughtExceptionMonitor', listener);
    Reflect.deleteProperty(globalThis, bridge);
    Reflect.set(process, 'resourcesPath', resources);
    process.argv = argv;
  });
  Reflect.set(globalThis, bridge, mocks);
  Reflect.set(process, 'resourcesPath', root);
  process.argv = busyLogonMarker
    ? [executable, '--login', '--background']
    : [...argv, '--agent-host'];
  if (busyLogonMarker) {
    const open = fsSync.openSync;
    let busy = true;
    t.mock.method(fsSync, 'openSync', (...args: Parameters<typeof open>) => {
      if (args[0] === layout.configurationPath) {
        events.push(busy ? 'marker.busy' : 'marker.open');
        if (busy) {
          busy = false;
          throw Object.assign(new Error('fixture sharing violation'), {
            code: 'EBUSY',
            syscall: 'open',
          });
        }
      }
      return open(...args);
    });
  }
  const request = (command: string, args: Record<string, unknown> = {}, requestId = randomUUID()) =>
    dispatch!({ protocol: 1, requestId, command, arguments: args }, () => true);
  (requireFixture(bundle) as typeof import('../../src/main/application.js')).startManager();
  await waitFor(async () => {
    if (events.some((event) => event.startsWith('startup.error.')))
      throw new Error(events.join(', '));
    if (!dispatch) return;
    const status = await request('app.status');
    return (status.result as { ready: boolean }).ready ? status : undefined;
  });
  if (!busyLogonMarker) assert.equal(events.includes('window.create'), false);
  return {
    events,
    layout,
    request,
    exitLogs,
    commandLog: () => commandLog,
    loseStorage: (error: Error) => {
      assert.ok(storageFailure, 'The portable storage lease must be admitted first.');
      storageFailure(error);
    },
    menu: () => menu,
  };
}

test('windowless tray stop and exit asks for real consent before stopping through the backend', async (t) => {
  const f = await fixture(t);
  const stop = f.menu().find((item) => item.label === tText.tray.stopExit);
  assert.ok(stop?.click);
  stop.click();
  await waitFor(() => f.events.find((event) => event.startsWith('dialog.')));
  assert.deepEqual(f.events.slice(0, 3), ['window.create', 'window.show', 'dialog.question']);
  await waitFor(() => f.events.find((event) => event === 'app.quit'));
  assert.ok(f.events.indexOf('script.stop') > f.events.indexOf('dialog.question'));
  assert.ok(f.events.indexOf('backend.shutdown') > f.events.indexOf('script.stop'));
});

test('a cancelled restart receipt remains readable during a fresh explicit retry of the same data move', async (t) => {
  const f = await fixture(t);
  const destination = path.join(f.layout.portableRoot, 'Moved');
  await prepareDataMove(f.layout, destination, async (directory) => {
    await fs.mkdir(directory);
  });
  const attempt = async () => {
    const review = await f.request('commands.preview', {
      command: 'storage.restart',
      arguments: {},
    });
    assert.equal(review.outcome, 'Confirmed', JSON.stringify(review));
    const token = (review.result as { confirmationToken: string }).confirmationToken;
    const result = await f.request('storage.restart', { confirmationToken: token });
    assert.equal(result.outcome, 'Accepted', JSON.stringify(result.result));
    return result;
  };
  const first = await attempt();
  const firstReceipt = first.result as { operationId: string; moveId: string };
  await first.afterReply!(false);
  assert.equal(
    (await f.request('operations.get', { operationId: firstReceipt.operationId })).code,
    'CancelledBeforeDispatch',
  );
  assert.equal(f.events.includes('app.relaunch'), false);
  const second = await attempt();
  const secondReceipt = second.result as typeof firstReceipt;
  assert.notEqual(firstReceipt.operationId, secondReceipt.operationId);
  assert.equal(firstReceipt.moveId, secondReceipt.moveId);
  assert.match(secondReceipt.moveId, /^[0-9a-f-]{36}$/);
  assert.equal(
    (await f.request('operations.get', { operationId: firstReceipt.operationId })).code,
    'CancelledBeforeDispatch',
  );
  await second.afterReply!(true);
  assert.ok(f.events.includes('app.relaunch'), f.events.join(', '));
});

test(
  'an actual Manager quit cannot shut down the backend after its placement save misses the deadline',
  { timeout: dispatchTimeout * 10 },
  async (t) => {
    let saving = false;
    let saved = false;
    let release!: () => void;
    const blocked = new Promise<void>((resolve) => {
      release = resolve;
    });
    let expire: () => void = () => {
      throw new Error('No shutdown deadline was scheduled.');
    };
    let scheduledMilliseconds: number | undefined;
    let timerCancelled = false;
    t.after(() => release());
    const f = await fixture(t, false, {
      savePlacement: async () => {
        saving = true;
        await blocked;
        saved = true;
      },
      scheduleShutdown: (callback, milliseconds) => {
        assert.equal(
          scheduledMilliseconds,
          undefined,
          'Only one shutdown deadline should be scheduled.',
        );
        expire = callback;
        scheduledMilliseconds = milliseconds;
        return () => {
          timerCancelled = true;
        };
      },
    });
    const shown = await f.request('app.show');
    assert.equal(shown.outcome, 'Confirmed', JSON.stringify(shown));
    assert.equal(
      scheduledMilliseconds,
      undefined,
      'Opening the Manager must not use the shutdown clock.',
    );
    const reply = await f.request('app.quit');
    assert.equal(reply.outcome, 'Accepted', JSON.stringify(reply));
    assert.equal(reply.code, 'ShutdownAccepted');
    assert.ok(reply.afterReply);
    const quitting = reply.afterReply(true);
    await waitFor(() => (saving ? true : undefined));
    assert.equal(scheduledMilliseconds, SHUTDOWN_DEADLINE_MS);
    assert.equal(f.events.includes('backend.shutdown'), false);
    expire();
    await quitting;
    assert.equal(timerCancelled, true);
    const timedOut = await f.request('app.status');
    assert.deepEqual(
      {
        ready: (timedOut.result as { ready: boolean }).ready,
        connection: (timedOut.result as { connection: string }).connection,
      },
      { ready: false, connection: 'failed' },
    );
    release();
    await waitFor(() => (saved ? true : undefined));
    // Let the original, now-expired save continuation reach the shutdown boundary.
    await new Promise<void>((resolve) => setImmediate(resolve));
    assert.equal(f.events.includes('backend.shutdown'), false, f.events.join(', '));
    assert.equal(f.events.includes('agent.close'), false, f.events.join(', '));
    assert.equal(f.events.includes('app.quit'), false, f.events.join(', '));
    assert.equal(f.events.includes('script.stop'), false, 'Independent scripts must keep running.');
    const afterSave = await f.request('app.status');
    assert.equal((afterSave.result as { ready: boolean }).ready, false);
    assert.equal((afterSave.result as { connection: string }).connection, 'failed');
  },
);

test('runtime storage lease loss retains finite diagnostic evidence before Manager exits', async (t) => {
  const f = await fixture(t, false, { captureCommands: true });
  const privateDetail = 'C:\\Private\\profile token=fixture-secret';
  f.loseStorage(
    new NativeStorageLeaseError(privateDetail, {
      schemaVersion: 1,
      phase: 'installation-paths',
      code: 'redirected-path',
    }),
  );
  assert.deepEqual(f.events.slice(-1), ['app.exit=65']);
  assert.equal(f.exitLogs.length, 1);
  const records = f.exitLogs[0]!.records.filter(
    (record) => record.command === 'storageLease' && record.phase === 'error',
  );
  assert.equal(
    records.length,
    1,
    'The evidence must already be written at the native exit boundary.',
  );
  assert.equal(records[0]!.source, 'lifecycle');
  assert.equal(records[0]!.errorName, 'NativeStorageLeaseError');
  assert.equal(records[0]!.helperPhase, 'installation-paths');
  assert.equal(records[0]!.helperCode, 'redirected-path');
  assert.equal(JSON.stringify(f.exitLogs).includes('fixture-secret'), false);
  const log = f.commandLog();
  assert.ok(log);
  for (const file of await fs.readdir(log.directory))
    assert.equal(
      (await fs.readFile(path.join(log.directory, file), 'utf8')).includes('fixture-secret'),
      false,
    );
  assert.equal(f.events.includes('script.stop'), false);
  assert.equal(f.events.includes('backend.shutdown'), false);
});

test('a tolerated data move refusal retains finite evidence after the current profile logger opens', async (t) => {
  const privateDetail = 'C:\\Private\\move token=fixture-secret';
  const f = await fixture(t, false, {
    captureCommands: true,
    dataMoveFailure: new NativeStorageLeaseError(privateDetail, {
      schemaVersion: 1,
      phase: 'portable-data-initialization',
      code: 'cloud-file-unavailable',
    }),
  });
  const log = f.commandLog();
  assert.ok(log);
  const records = log
    .snapshot('errors')
    .records.filter((record) => record.command === 'storage:restart' && record.phase === 'error');
  assert.equal(records.length, 1);
  assert.equal(records[0]!.errorName, 'NativeStorageLeaseError');
  assert.equal(records[0]!.helperPhase, 'portable-data-initialization');
  assert.equal(records[0]!.helperCode, 'cloud-file-unavailable');
  const files = log.exportFiles();
  assert.deepEqual(files.issues, []);
  assert.equal(JSON.stringify(files).includes('fixture-secret'), false);
  for (const file of await fs.readdir(log.directory))
    assert.equal(
      (await fs.readFile(path.join(log.directory, file), 'utf8')).includes('fixture-secret'),
      false,
    );
  assert.equal(f.events.includes('app.relaunch'), false);
  assert.equal(f.events.includes('app.exit=65'), false);
  assert.equal(
    await fs.readFile(path.join(f.layout.profileDirectory, 'settings.json'), 'utf8'),
    '{"preserved":true}',
  );
  const marker = JSON.parse(await fs.readFile(f.layout.configurationPath!, 'utf8')) as {
    dataDirectory: string;
  };
  assert.equal(marker.dataDirectory, 'Data');
  await assert.rejects(fs.stat(path.join(f.layout.portableRoot, 'Moved')), { code: 'ENOENT' });
});

test('registered portable logon recovers a busy configuration before any native bootstrap or instance lock', async (t) => {
  if (process.platform !== 'win32') return t.skip('Packaged Windows logon only');
  const f = await fixture(t, true);
  assert.deepEqual(f.events.slice(0, 5), [
    'marker.busy',
    'marker.open',
    'storage.initialize',
    'userData=' + f.layout.chromiumDirectory,
    'instance.lock',
  ]);
  assert.equal(f.events.filter((event) => event === 'storage.initialize').length, 1);
});

test('installLatest returns a durable receipt with the wire ID and dispatches the pinned update once', async (t) => {
  const bytes = Buffer.from('Inert update archive for a local fixture.');
  const item: RemoteUpdateRelease = {
    id: 'github:42',
    version: '1.2.2',
    publishedAt: '2026-11-09T00:00:00Z',
    notes: 'Fixture stable release',
    commit: 'a'.repeat(40),
    assetId: 43,
    sha256: createHash('sha256').update(bytes).digest('hex'),
    sha512: createHash('sha512').update(bytes).digest('base64'),
    bytes: bytes.length,
    archiveName: 'AHK-Manager-1.2.2-Portable-x64.zip',
    deployment: 'portable',
  };
  item.manifestText = JSON.stringify({
    schemaVersion: 1,
    updateProtocol: 2,
    version: item.version,
    commit: item.commit,
    publishedAt: item.publishedAt,
    notes: item.notes,
    archive: {
      name: item.archiveName,
      bytes: item.bytes,
      sha256: item.sha256,
      sha512: item.sha512,
    },
  });
  let lookups = 0;
  let failLookup = false;
  let latest: RemoteUpdateRelease[] = [];
  const downloads: Array<string> = [];
  const applies: Array<{ id: string; version: string }> = [];
  let service: UpdateService | undefined;
  const f = await fixture(t, false, {
    createUpdates: async (boundary, store) => {
      await store.saveChannel('test');
      service = new UpdateService({
        ...boundary,
        version: '1.2.1',
        store,
        releases: async () => [],
        stableReleases: async () => {
          lookups++;
          if (failLookup) throw new Error('fixture stable check failure');
          return latest;
        },
        download: async (release) => {
          assert.equal(release.manifestText, item.manifestText);
          downloads.push(release.id);
          const target = path.join(store.root, 'downloads');
          await store.safeDirectory(target, true);
          const directory = await fs.mkdtemp(path.join(target, 'portable-'));
          const archive = path.join(directory, 'archive.zip');
          await fs.writeFile(archive, bytes);
          await fs.writeFile(path.join(directory, 'release.json'), release.manifestText!);
          return archive;
        },
        apply: async (id, target) => {
          applies.push({ id, version: target.version });
        },
      });
      await service.initialize();
      return service;
    },
  });
  assert.equal(
    (await f.request('updates.installLatest', { version: '1.2.2' })).code,
    'InvalidArguments',
  );
  assert.equal(
    (await f.request('commands.preview', { command: 'updates.installLatest', arguments: {} })).code,
    'UnknownCommand',
  );
  assert.equal(lookups, 0);
  assert.deepEqual(await f.request('updates.installLatest'), {
    outcome: 'Confirmed',
    code: 'NoNewerStable',
    result: null,
  });
  failLookup = true;
  assert.equal((await f.request('updates.installLatest')).code, 'UpdateCheckFailed');
  failLookup = false;
  latest = [item];
  const operationId = randomUUID();
  const reply = await f.request('updates.installLatest', {}, operationId);
  assert.equal(reply.outcome, 'Accepted', JSON.stringify(reply));
  assert.equal(reply.code, 'OperationAccepted');
  assert.deepEqual(reply.result, {
    schemaVersion: 1,
    operationId,
    generation: (reply.result as { generation: string }).generation,
    kind: 'Update',
    version: item.version,
  });
  assert.equal((await f.request('operations.get', { operationId })).code, 'OperationInProgress');
  assert.equal(
    (await f.request('updates.installLatest', {}, operationId)).code,
    'OperationBlocked',
  );
  assert.deepEqual(downloads, []);
  assert.deepEqual(applies, []);
  assert.ok(reply.afterReply);
  await reply.afterReply(true);
  await reply.afterReply(true);
  assert.deepEqual(downloads, [item.id]);
  assert.deepEqual(applies, [{ id: operationId, version: item.version }]);
  assert.equal(lookups, 3);
  assert.equal(service?.snapshot.channel, 'test');
  assert.equal(f.events.includes('script.stop'), false);
  assert.equal(f.events.includes('window.create'), false);
});
