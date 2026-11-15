import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { EventEmitter } from 'node:events';
import fsSync from 'node:fs';
import fs from 'node:fs/promises';
import { createRequire } from 'node:module';
import path from 'node:path';
import test, { type TestContext } from 'node:test';
import type { AgentReply, AgentRequest } from '../../src/main/agent-transport.js';
import { resolveDeployment } from '../../src/main/storage/deployment.js';
import { prepareDataMove } from '../../src/main/storage/data-migration.js';
import { UpdateStore } from '../../src/main/updates/update-store.js';
import { hostText as tText } from '../../src/shared/host-en-US.js';
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

async function fixture(t: TestContext, busyLogonMarker = false) {
  const staging = process.env.AHK_DIAGNOSTICS_TEST_ROOT;
  assert.ok(staging && path.isAbsolute(staging));
  const root = await fs.mkdtemp(path.join(staging, 'agent-application-'));
  t.after(() => fs.rm(root, { recursive: true, force: true }));
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
  const events: string[] = [];
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
      events.push('app.exit=' + code);
    },
    relaunch: () => {
      events.push('app.relaunch');
    },
  });
  const store = new UpdateStore(layout.updatesDirectory);
  const mocks = {
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
      acquirePortableStorageLease: async () => ({ close: async () => {} }),
    },
    './storage/data-move-lease.js': {
      withDataMoveLease: async (_layout: unknown, work: () => Promise<unknown>) => work(),
    },
    './logging/create-command-log.js': { createCommandLog: () => null },
    './logging/startup-failure.js': {
      recordStartupFailure: ({ error }: { error: unknown }) => {
        events.push('startup.error.' + String(error));
        return {};
      },
      startupFailureDirectory: () => root,
    },
    './updates/application-updates.js': {
      createApplicationUpdates: async () => ({ service: { busy: false, snapshot: {} }, store }),
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
  const request = (command: string, args: Record<string, unknown> = {}) =>
    dispatch!({ protocol: 1, requestId: randomUUID(), command, arguments: args }, () => true);
  (requireFixture(bundle) as typeof import('../../src/main/application.js')).startManager();
  await waitFor(async () => {
    if (events.some((event) => event.startsWith('startup.error.')))
      throw new Error(events.join(', '));
    if (!dispatch) return;
    const status = await request('app.status');
    return (status.result as { ready: boolean }).ready ? status : undefined;
  });
  if (!busyLogonMarker) assert.equal(events.includes('window.create'), false);
  return { events, layout, request, menu: () => menu };
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
    assert.equal(review.outcome, 'Confirmed');
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
