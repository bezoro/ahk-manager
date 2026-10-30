import {
  app,
  clipboard,
  ipcMain,
  dialog,
  Menu,
  nativeTheme,
  protocol,
  screen,
  shell,
  systemPreferences,
  type BrowserWindow,
  type Tray,
} from 'electron';
import { scriptFileStamp } from './script-files.js';
import path from 'node:path';
import fs from 'node:fs/promises';
import { AsyncLocalStorage } from 'node:async_hooks';
import { randomUUID } from 'node:crypto';
import { BackendClient } from './backend.js';
import { ManagerController } from './controller.js';
import {
  administrationCli,
  startupAdoptionCli,
  startupAdoptionBlocksInteraction,
  LifecycleIntent,
  StopExitIntent,
  SHUTDOWN_DEADLINE_MS,
  orderlyShutdown,
  shutdownWithinDeadline,
  reportStartupWithContinuation,
  configuredLoginElevation,
} from './lifecycle.js';
import {
  AdministrationPreflight,
  ElevationCoordinator,
  administrationDocumentReady,
} from './elevation.js';
import type { PageDiagnostics } from './diagnostics.js';
import { hostText as t } from '../shared/host-en-US.js';
import { APP_URL, DocumentGuard, packagedBackend, type DocumentLease } from './security.js';
import { restorePlacement } from './placement.js';
import type {
  ElevationStatus,
  ManagerSnapshot,
  OperationResult,
  StartupContinuationOffer,
  ThemeState,
  WindowPlacement,
} from '../shared/contracts.js';
import type { UpdateService } from './updates/update-service.js';
import type { UpdateStore, CachedInstaller } from './updates/update-store.js';
import { compareVersions } from './updates/github-updates.js';
import { verifyCachedInstallerSignature } from './updates/electron-update-download.js';
import { prepareUpdate, type PreparedUpdate } from './updates/update-handoff.js';
import { shutdownAndCommitUpdate } from './updates/update-shutdown.js';

import { serveAssets, lockSession } from './desktop/assets.js';
import { createMainWindow } from './desktop/main-window.js';
import { createNativeDialogs } from './desktop/native-dialogs.js';
import { DesktopTheme } from './desktop/theme.js';
import { createWindowDiagnostics } from './desktop/window-diagnostics.js';
import { createTrayIcon, trayMenu } from './desktop/tray.js';
import { registerHandlers } from './ipc/register-handlers.js';
import { createApplicationUpdates } from './updates/application-updates.js';
import { verifyCachedPortablePackageSignature } from './updates/portable-update-signature.js';
import { DataLocationController } from './desktop/data-location.js';
import { initializePortableStorage } from './desktop/storage-bootstrap.js';
import { ensurePortableSearchShortcut } from './desktop/portable-search-shortcut.js';
import { resolveDeployment, samePath, type DeploymentLayout } from './storage/deployment.js';
import {
  canKeepCurrentData,
  completePendingDataMove,
  importLegacyUpdateChannel,
} from './storage/data-migration.js';
import { withDataMoveLease } from './storage/data-move-lease.js';
import {
  type CommandLog,
  type CommandLogBoundary,
  type CommandDetails,
  type CommandSource,
} from './logging/command-log.js';
import { createCommandLog } from './logging/create-command-log.js';
import {
  acquirePortableStorageLease,
  type PortableStorageLease,
} from './storage/portable-storage-lease.js';

/** Composes desktop, backend and lifecycle services for one Manager process. */
export function startManager(): void {
  app.commandLine.appendSwitch('lang', 'en-US');
  protocol.registerSchemesAsPrivileged([
    {
      scheme: 'ahk-manager',
      privileges: { standard: true, secure: true, supportFetchAPI: true, corsEnabled: true },
    },
  ]);
  app.setName(t.title);
  app.setAppUserModelId('Bezoro.AhkManager.Electron');
  const localAppData =
    process.env.LOCALAPPDATA ?? path.join(app.getPath('home'), 'AppData', 'Local');
  let deployment: DeploymentLayout;
  let storageError: string | null = null;
  let storageRestarting = false;
  try {
    deployment = resolveDeployment(app.getPath('exe'), localAppData);
    initializePortableStorage(deployment);
  } catch (error) {
    const quiet =
      !process.argv.includes('--foreground') &&
      !process.argv.includes('--request-administrator') &&
      (process.argv.includes('--login') || process.argv.includes('--background'));
    if (quiet) {
      app.exit(65);
      return;
    }
    void app.whenReady().then(() => {
      dialog.showErrorBox(t.title, safeError(error));
      app.exit(65);
    });
    return;
  }
  // Chromium storage never opens or copies a framework's previous operational profile.
  app.setPath(
    'userData',
    deployment.chromiumDirectory ??
      path.join(app.getPath('appData'), 'Bezoro', 'AhkManagerElectron', 'Chromium'),
  );
  let window: BrowserWindow | null = null;
  let guard: DocumentGuard | null = null;
  let backend: BackendClient | null = null;
  let controller: ManagerController | null = null;
  let tray: Tray | null = null;
  let trayRegistered = false;
  let diagnostics: PageDiagnostics | null = null;
  let diagnosticSelection: string | null = null;
  let ready = false;
  let rendererReady = false;
  let documentLease: DocumentLease | null = null;
  const initialAdministration = administrationCli(process.argv);
  const initialAdoption = startupAdoptionCli(process.argv);
  const quietAdoption =
    initialAdoption.descriptor !== null && process.argv.includes('--background');
  let adoptionPending = initialAdoption.descriptor !== null || initialAdoption.invalid;
  let continuationOffer: StartupContinuationOffer | null = null;
  let administrativeCliPending = initialAdministration === 'request';
  const lifecycle = new LifecycleIntent(
    !adoptionPending && initialAdministration === 'none' && process.argv.includes('--login'),
    (adoptionPending && !quietAdoption) ||
      initialAdministration !== 'none' ||
      process.argv.includes('--foreground'),
    quietAdoption,
  );
  const preflight = new AdministrationPreflight();
  let administration: ElevationCoordinator | null = null;
  let administrationLease: DocumentLease | null = null;
  let quietStartupTransfer = false;
  const stopExit = new StopExitIntent();
  let stopExitProgress = { done: 0, total: 0 };
  let quitting = false;
  let quitComplete = false;
  let updates: UpdateService | null = null;
  let updateStore: UpdateStore | null = null;
  let updateApplying = false;
  let updateShutdownInFlight: Promise<void> | null = null;
  let dataLocation: DataLocationController | null = null;
  let storageLease: PortableStorageLease | null = null;
  let storageClosing: Promise<void> | null = null;
  let storageClosed = false;
  let commandLog: CommandLog | null = null;
  const commandTrace: Pick<CommandLogBoundary, 'run'> = { run: trace };
  const documentScope = new AsyncLocalStorage<() => boolean>();
  const alive = () =>
    !quitting && (documentScope.getStore()?.() ?? (window !== null && !window.isDestroyed()));
  const desktopTheme = new DesktopTheme(
    () => window,
    () => controller?.state.snapshot?.settings,
    (state) => guard?.send('manager:theme', state),
  );
  const theme = (): ThemeState => desktopTheme.state;
  const updateTheme = desktopTheme.update;
  const nativeDialogs = createNativeDialogs(() => window, alive);
  const pick: typeof nativeDialogs.files = (kind) =>
    trace('ui', 'pick.' + kind, () => nativeDialogs.files(kind));
  const confirm: typeof nativeDialogs.confirm = (title, detail) =>
    trace('ui', 'confirm', () => nativeDialogs.confirm(title, detail));
  const single = app.requestSingleInstanceLock();
  if (!single) app.quit();
  else {
    app.on('second-instance', (_event, argv) => {
      void trace('lifecycle', 'secondInstance', () => {
        if (
          adoptionPending ||
          argv.some(
            (value) => value === '--startup-adoption' || value.startsWith('--startup-adoption='),
          )
        ) {
          if (!argv.includes('--login') && !argv.includes('--background')) {
            lifecycle.requestExplicitActivation();
            show();
          }
          return;
        }
        if (initialAdoption.descriptor !== null && argv.includes('--login')) {
          return;
        }
        const administrative = administrationCli(argv);
        if (administrative !== 'none') {
          lifecycle.requestExplicitActivation();
          show();
          if (administrative === 'invalid')
            dialog.showErrorBox(t.title, t.administrationCliInvalid);
          else if (ready) void requestAdministration(true);
          else administrativeCliPending = true;
          return;
        }
        if (argv.includes('--login')) {
          lifecycle.requestLogin();
          if (ready) void runLogin();
        } else {
          lifecycle.requestExplicitActivation();
          show();
        }
      });
    });
    app.on('activate', () => {
      void trace('lifecycle', 'activate', () => {
        lifecycle.requestExplicitActivation();
        show();
      });
    });
    app.on('before-quit', (event) => {
      if (!quitComplete) {
        event.preventDefault();
        void quit();
      }
    });
    app.on('window-all-closed', () => {
      if (!quitting) void quit();
    });
    app.on('will-quit', (event) => {
      commandLog?.close();
      if (!storageLease || storageClosed) return;
      event.preventDefault();
      if (storageClosing) return;
      storageClosing = storageLease.close().then(
        () => {
          storageClosed = true;
          // Leave the cancelled native quit callback before requesting exit again.
          setImmediate(() => app.quit());
        },
        () => app.exit(65),
      );
    });
    const storageReady = prepareStorage();
    void Promise.all([app.whenReady(), storageReady])
      .then(() => {
        if (!storageRestarting) return start();
      })
      .catch((error) => {
        if (!lifecycle.quietStartup) dialog.showErrorBox(t.title, safeError(error));
        quitComplete = true;
        app.quit();
      });
  }
  function safeError(error: unknown): string {
    return error instanceof Error ? error.message.slice(0, 4096) : t.unknownError;
  }
  async function trace<T>(
    source: CommandSource,
    command: string,
    work: () => T | Promise<T>,
    details?: CommandDetails,
  ): Promise<T> {
    return commandLog ? commandLog.run(source, command, work, details) : work();
  }
  function recordFailure(command: string, error: unknown): void {
    void trace('lifecycle', command, () => {
      throw error;
    }).catch(() => {});
  }
  async function prepareStorage(): Promise<void> {
    try {
      const previous = deployment;
      try {
        deployment = await withDataMoveLease(previous, (boundary) =>
          completePendingDataMove(previous, boundary),
        );
      } catch (error) {
        const fallback = resolveDeployment(app.getPath('exe'), localAppData);
        if (!(await canKeepCurrentData(fallback))) throw error;
        deployment = fallback;
        storageError = safeError(error);
      }
      if (!samePath(previous.dataDirectory, deployment.dataDirectory)) {
        // Electron may have initialized Chromium while the asynchronous copy ran.
        // A fresh process selects the committed data path before Chromium starts.
        app.relaunch({ execPath: deployment.launchExecutable, args: ['--foreground'] });
        storageRestarting = true;
        quitComplete = true;
        app.exit(0);
        return;
      }
      // Check the authoritative pointer again after acquiring the selected lock.
      const current = resolveDeployment(app.getPath('exe'), localAppData);
      if (!samePath(current.dataDirectory, deployment.dataDirectory))
        throw new Error('The data location changed during startup. Open Manager again.');
      if (deployment.mode === 'portable')
        storageLease = await acquirePortableStorageLease(deployment, (error) => {
          // Losing namespace protection must stop writes and cannot authorize an update.
          controller?.setConnection('failed', safeError(error));
          quitting = true;
          app.exit(65);
        });
      try {
        await importLegacyUpdateChannel(deployment);
      } catch (error) {
        storageError = safeError(error);
      }
      // Logging avoids migration destinations. A pending data move is completed above
      // before any log files are opened or written.
      commandLog = createCommandLog(deployment, localAppData);
      process.on('uncaughtExceptionMonitor', (error) => recordFailure('uncaughtException', error));
    } catch (error) {
      storageError = safeError(error);
      throw error;
    }
  }
  function show(): void {
    if (!window || window.isDestroyed() || quitting) return;
    lifecycle.showWindow(window);
    deliverPreferences();
  }
  function deliverPreferences(): void {
    if (lifecycle.takePreferences(ready && rendererReady && !!guard?.connected))
      guard?.send('manager:preferences', null);
  }
  function showPreferences(): void {
    if (administration?.active || dataLocation?.active) return;
    lifecycle.requestPreferences();
    show();
    deliverPreferences();
  }
  function placement(): WindowPlacement {
    if (!window || window.isDestroyed())
      return (
        controller?.state.snapshot?.settings.window ?? {
          x: 100,
          y: 100,
          width: 1200,
          height: 780,
          maximized: false,
          dpi: 96,
        }
      );
    const maximized = window.isMaximized() || lifecycle.pendingMaximizedRestore;
    const bounds = window.isMaximized() ? window.getNormalBounds() : window.getBounds();
    const physical = screen.dipToScreenRect(window, bounds);
    return { ...physical, maximized, dpi: screen.getDisplayMatching(bounds).scaleFactor * 96 };
  }
  async function configureUpdates(): Promise<void> {
    const configured = await createApplicationUpdates(
      {
        preflight: (operationId, phase) => {
          if (dataLocation?.state.pendingDataDirectory)
            throw new Error('Restart Manager to complete the data move before updating.');
          const lease = documentLease;
          return preflight.check(
            operationId,
            phase,
            (request) => guard?.send('manager:administrationPreflight', request),
            () =>
              !!lease &&
              !!guard?.current(lease) &&
              !!window?.isFocused() &&
              !window.isDestroyed() &&
              !quitting &&
              !updateApplying &&
              !dataLocation?.active &&
              !updateShutdownInFlight &&
              !adoptionPending &&
              !administration?.active &&
              !stopExit.active &&
              !controller?.state.busy &&
              !controller?.state.administrationPending,
          );
        },
        confirm: (release) =>
          confirm(
            compareVersions(release.version, app.getVersion()) < 0
              ? 'Return to version ' + release.version + '?'
              : 'Update to version ' + release.version + '?',
            'The manager will close and reopen. Running scripts will keep running, and your settings will be preserved.',
          ),
        apply: applyUpdate,
        changed: (state) => guard?.send('manager:updates', state),
      },
      () => !quitting && !updateApplying,
      {
        deployment: deployment.mode,
        root: deployment.updatesDirectory,
        currentCommit: await runningCommit(),
        commandLog: commandTrace,
      },
    );
    updates = configured.service;
    updateStore = configured.store;
  }
  async function runningCommit(): Promise<string | undefined> {
    const metadata = JSON.parse(
      await fs.readFile(path.join(app.getAppPath(), 'package.json'), 'utf8'),
    ) as { sourceCommit?: unknown };
    return typeof metadata.sourceCommit === 'string' && /^[a-f0-9]{40}$/.test(metadata.sourceCommit)
      ? metadata.sourceCommit
      : undefined;
  }
  async function restartForDataMove(): Promise<void> {
    const operationId = randomUUID();
    let shutdownStarted = false;
    const lease = documentLease;
    const check = (phase: 'begin' | 'commit') =>
      preflight.check(
        operationId,
        phase,
        (request) => guard?.send('manager:administrationPreflight', request),
        () =>
          !!lease &&
          !!guard?.current(lease) &&
          !!window?.isFocused() &&
          !window.isDestroyed() &&
          !quitting &&
          !updateApplying &&
          !updateShutdownInFlight &&
          !adoptionPending &&
          !administration?.active &&
          !stopExit.active &&
          !updates?.busy &&
          dataLocation?.active === true &&
          controller?.state.busy === false,
      );
    try {
      if (!(await check('begin')))
        throw new Error(
          'Save or discard your changes and wait for operations to finish before restarting Manager.',
        );
      await shutdownWithinDeadline(
        {
          drain: async () => {
            await controller?.drain();
          },
          backendPresent: () => backend !== null,
          backendExited: () => backend?.exited ?? true,
          persistPlacement: async () => {
            await controller?.savePlacement(true);
          },
          shutdownBackend: async () => {
            if (!backend) return;
            shutdownStarted = true;
            const closing = backend.shutdown().then(() => {});
            updateShutdownInFlight = closing;
            try {
              await closing;
            } finally {
              if (updateShutdownInFlight === closing) updateShutdownInFlight = null;
            }
          },
          reportPlacementFailure: () => {},
        },
        SHUTDOWN_DEADLINE_MS,
        {
          beforeShutdown: async () => {
            if (!(await check('commit')))
              throw new Error('The restart was cancelled because the application state changed.');
          },
          cancelled: 'The data location restart was cancelled.',
          timedOut: 'Manager shutdown was not confirmed in time. The data location was preserved.',
          unconfirmed: 'Manager shutdown was not confirmed. The data location was preserved.',
        },
      );
      app.relaunch({ execPath: deployment.launchExecutable, args: ['--foreground'] });
      quitting = true;
      tray?.destroy();
      quitComplete = true;
      app.quit();
    } catch (error) {
      if (shutdownStarted || backend?.exited) controller?.setConnection('failed', safeError(error));
      show();
      dialog.showErrorBox('Data location', safeError(error));
      throw error;
    }
  }
  async function applyUpdate(operationId: string, target: CachedInstaller): Promise<void> {
    if (
      !updateStore ||
      !window ||
      updateApplying ||
      updateShutdownInFlight ||
      quitting ||
      adoptionPending ||
      administration?.active ||
      stopExit.active ||
      dataLocation?.active
    )
      throw new Error('Wait for manager operations to finish before updating.');
    updateApplying = true;
    controller?.setAdministrationPending(true);
    let prepared: PreparedUpdate | null = null;
    let shutdownStarted = false;
    try {
      if (deployment.mode === 'portable')
        await verifyCachedPortablePackageSignature(target.installerPath);
      else await verifyCachedInstallerSignature(target.installerPath);
      const created = app
        .getAppMetrics()
        .find((metric) => metric.pid === process.pid)?.creationTime;
      if (!created || !Number.isFinite(created))
        throw new Error('The manager session could not be verified for this update.');
      prepared = await prepareUpdate(
        updateStore,
        operationId,
        target,
        path.dirname(app.getPath('exe')),
        new Date(created).toISOString(),
      );
      shutdownStarted = true;
      await shutdownAndCommitUpdate(
        {
          drain: async () => {
            await controller?.drain();
          },
          backendPresent: () => backend !== null,
          backendExited: () => backend?.exited ?? true,
          persistPlacement: async () => {
            await controller?.savePlacement(true);
          },
          shutdownBackend: async () => {
            if (!backend) return;
            const closing = backend.shutdown().then(() => {});
            updateShutdownInFlight = closing;
            try {
              await closing;
            } finally {
              if (updateShutdownInFlight === closing) updateShutdownInFlight = null;
            }
          },
          reportPlacementFailure: () => {},
        },
        SHUTDOWN_DEADLINE_MS,
        () => prepared!.commit(),
      );
      quitting = true;
      tray?.destroy();
      quitComplete = true;
      app.quit();
    } catch (error) {
      let cancellationError: unknown;
      try {
        await prepared?.cancel();
      } catch (cancelError) {
        cancellationError = cancelError;
      } finally {
        updateApplying = false;
        controller?.setAdministrationPending(false);
        if (shutdownStarted && backend)
          controller?.setConnection(
            'failed',
            'The update was cancelled without confirmed shutdown. Wait and reconnect the backend before continuing.',
          );
      }
      if (cancellationError)
        throw new Error(safeError(error) + ' The update cancellation could not be recorded.', {
          cause: cancellationError,
        });
      throw error;
    }
  }
  function start(): Promise<void> {
    return trace('lifecycle', 'start', startApplication, { version: app.getVersion() });
  }
  async function startApplication(): Promise<void> {
    if (initialAdoption.invalid) throw new Error(t.adoptionInvalid);
    if (process.platform !== 'win32' || !app.isPackaged) throw new Error(t.windowsPackagedRequired);
    await serveAssets();
    lockSession();
    window = createMainWindow();
    diagnostics = createWindowDiagnostics(window, () => ({
      rendererPid: window?.webContents.getOSProcessId(),
      url: window?.webContents.getURL(),
      viewport: window?.getContentBounds(),
      material: desktopTheme.backgroundMaterial,
      theme: controller?.state.snapshot?.settings.theme ?? 'System',
      revision: controller?.state.snapshot?.revision ?? null,
      selectedId: diagnosticSelection,
      scriptCount: controller?.state.snapshot?.scripts.length ?? 0,
    }));
    Menu.setApplicationMenu(null);
    desktopTheme.initialize(window);
    guard = new DocumentGuard(window.webContents);
    window.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));
    window.webContents.on('will-navigate', (event) => event.preventDefault());
    window.webContents.on('will-frame-navigate', (event) => event.preventDefault());
    window.webContents.on('will-attach-webview', (event) => event.preventDefault());
    window.webContents.on('did-start-navigation', (_event, _url, isInPlace, isMainFrame) => {
      if (isMainFrame && !isInPlace) {
        rendererReady = false;
        documentLease = null;
        preflight.invalidate();
        guard?.invalidate();
        controller?.updateSelection(null);
      }
    });
    window.webContents.on('render-process-gone', () => {
      rendererReady = false;
      documentLease = null;
      preflight.invalidate();
      guard?.invalidate();
      controller?.updateSelection(null);
    });
    window.webContents.on('did-finish-load', deliverPreferences);
    window.on('close', (event) => {
      if (quitComplete) return;
      event.preventDefault();
      void trace('lifecycle', 'closeWindow', closeWindow);
    });
    nativeTheme.on('updated', updateTheme);
    systemPreferences.on('accent-color-changed', updateTheme);
    const native = {
      files: pick,
      confirm,
      placement,
      alive,
      fileStamp: scriptFileStamp,
      trash: (target: string) => shell.trashItem(path.win32.normalize(target)),
    };
    controller = new ManagerController(
      { request: () => Promise.reject(new Error('Backend starting')) },
      native,
      (state) => {
        guard?.send('manager:state', state);
        updateTheme();
        updateTray();
      },
    );
    administration = new ElevationCoordinator({
      eligible: () =>
        !adoptionPending &&
        !quitting &&
        !stopExit.active &&
        controller?.state.connection === 'ready' &&
        controller.state.authorityMode === 'Normal' &&
        !controller.state.busy &&
        (!quietStartupTransfer ||
          (lifecycle.quietStartup &&
            configuredLoginElevation(controller.state.snapshot?.settings) &&
            controller.state.snapshot?.readOnly === false &&
            controller.state.snapshot.effectsAvailable)) &&
        !!backend?.ready,
      confirm: () => confirm(t.administrationTitle, t.administrationDetail),
      preflight: (operationId, phase) => {
        const lease = administrationLease;
        return preflight.check(
          operationId,
          phase,
          (request) => guard?.send('manager:administrationPreflight', request),
          () =>
            administrationDocumentReady({
              currentDocument: !!lease && !!guard?.current(lease),
              windowAlive: !!window && !window.isDestroyed(),
              focused: window?.isFocused() === true,
              quietStartup:
                quietStartupTransfer &&
                lifecycle.quietStartup &&
                configuredLoginElevation(controller?.state.snapshot?.settings),
            }),
        );
      },
      quiesce: (value) => controller?.setAdministrationPending(value),
      drain: async () => {
        await controller?.drain();
      },
      persistPlacement: async () => {
        await controller?.savePlacement(true);
      },
      request: (action, payload, timeoutMs) => {
        if (!backend) throw new Error(t.backendUnavailable);
        return backend.request<ElevationStatus>(action, payload, timeoutMs);
      },
      quitAfterAcknowledgement: () => quit(true),
    });
    dataLocation = new DataLocationController(deployment, storageError, {
      ready: () =>
        !!backend?.ready &&
        controller?.state.connection === 'ready' &&
        !quitting &&
        !updateApplying &&
        !updateShutdownInFlight &&
        !updates?.busy &&
        !adoptionPending &&
        !administration?.active &&
        !stopExit.active &&
        !controller.state.busy,
      alive,
      quiesce: (value) => controller?.setAdministrationPending(value),
      drain: async () => {
        await controller?.drain();
      },
      chooseDirectory: async (current) => {
        if (!window) throw new Error('Manager is not ready.');
        const choice = await dialog.showOpenDialog(window, {
          title: 'Choose data folder',
          buttonLabel: 'Use this folder',
          defaultPath: current,
          properties: ['openDirectory', 'createDirectory'],
        });
        return choice.canceled ? null : (choice.filePaths[0] ?? null);
      },
      preparePrivateDirectory: async (directory) => {
        if (!backend?.ready) throw new Error(t.backendUnavailable);
        const result = await backend.request<OperationResult>('preparePortableDataDirectory', {
          path: directory,
        });
        if (result.outcome !== 'Confirmed')
          throw new Error(result.detail ?? 'The data folder could not be prepared.');
      },
      openDirectory: async (directory) => {
        const error = await shell.openPath(directory);
        if (error) throw new Error(error);
      },
      restart: restartForDataMove,
    });
    await configureUpdates();
    registerIpc();
    await window.loadURL(APP_URL);
    createTray();
    updateTheme();
    await startBackend();
    const saved = controller.state.snapshot?.settings.window;
    if (saved) {
      const dip = screen.screenToDipRect(window, {
        x: saved.x,
        y: saved.y,
        width: saved.width,
        height: saved.height,
      });
      window.setBounds(restorePlacement({ ...saved, ...dip, dpi: 96 }, screen.getAllDisplays()));
      lifecycle.deferMaximizedRestore(saved.maximized);
      if (window.isVisible()) show();
    }
    ready = true;
    if (initialAdministration === 'invalid')
      dialog.showErrorBox(t.title, t.administrationCliInvalid);
    const wantsAdministrator =
      controller.state.snapshot?.settings.launchManagerAsAdministrator === true;
    const canInitialize =
      !initialAdoption.descriptor &&
      !adoptionPending &&
      controller.state.connection === 'ready' &&
      controller.state.snapshot?.readOnly === false &&
      controller.state.snapshot.effectsAvailable;
    let setupDeferred = false;
    if (
      administrativeCliPending ||
      (canInitialize &&
        wantsAdministrator &&
        controller.state.authorityMode === 'Normal' &&
        (!lifecycle.quietStartup ||
          lifecycle.takeAutomaticElevation(controller.state.snapshot?.settings)))
    ) {
      administrativeCliPending = false;
      const quiet = lifecycle.quietStartup;
      if (!quiet) show();
      const result = await requestAdministration(true, lifecycle.loginRequested, quiet);
      if (result.outcome === 'Confirmed') {
        if (quitting || quitComplete) return;
      } else {
        setupDeferred = true;
        if (window && !quiet)
          await dialog.showMessageBox(window, {
            type: 'warning',
            title: t.administrationTitle,
            message: t.administrationSetupDeferred,
            detail: result.detail ?? t.loginFailure(result.code),
            buttons: [t.confirm],
            defaultId: 0,
            cancelId: 0,
            noLink: true,
          });
      }
    }
    if (canInitialize && !setupDeferred && backend?.ready) {
      // Startup configuration failures are already published in state and Activity.
      await backend.request<OperationResult>('initializeStartup', {});
    }
    await runLogin();
    if (backend) registerPortableApplication(backend);
    if (
      lifecycle.shouldShowAtStartup(
        controller.state.snapshot?.settings,
        trayRegistered,
        controller.state.connection !== 'ready' || controller.state.snapshot?.readOnly === true,
        controller.state.snapshot?.issues,
      )
    )
      show();
    if (window.isVisible()) diagnostics?.initialReady();
  }
  async function startBackend(): Promise<void> {
    if (administration?.active) throw new Error(t.administrationPending);
    if (stopExit.active) throw new Error(t.stopExitBusy);
    if (quitting) throw new Error(t.closing);
    if (!controller) throw new Error('Controller unavailable');
    if (backend && !backend.exited) throw new Error(t.backendStillAlive(backend.pid));
    backend = new BackendClient({
      executable: packagedBackend(path.dirname(process.execPath)),
      hostExecutable: process.execPath,
      commandLog: commandTrace,
      ...(initialAdoption.descriptor ? { startupAdoption: initialAdoption.descriptor } : {}),
    });
    if (adoptionPending) controller.setAdministrationPending(true);
    controller.setAuthority(backend);
    controller.setConnection('starting');
    backend.on('snapshot', (snapshot: ManagerSnapshot) => controller?.setSnapshot(snapshot));
    backend.on('unavailable', (error: Error) => {
      if (!quitting) controller?.setConnection('failed', error.message);
    });
    try {
      await trace('backend', 'start', () => backend!.start());
      controller.setHostAuthority(backend.authorityMode);
      const initialized = await backend.request<OperationResult>('initializeProfile', {});
      if (initialized.outcome !== 'Confirmed')
        throw new Error(initialized.detail ?? 'The application profile could not be prepared.');
      if (
        deployment.mode !== 'portable' &&
        backend.profileRoot &&
        !lifecycle.loginRequested &&
        !lifecycle.quietStartup
      ) {
        const exists = await fs.access(path.join(backend.profileRoot, 'settings.json')).then(
          () => true,
          () => false,
        );
        if (!exists && window) {
          const choice = await dialog.showMessageBox(window, {
            type: 'question',
            title: t.welcomeTitle,
            message: t.welcomeQuestion,
            detail: t.welcomeDetail,
            buttons: [t.newProfile, t.chooseWinUiProfile],
            defaultId: 0,
            cancelId: 0,
            noLink: true,
          });
          if (choice.response === 1) await controller.migrateProfile(true);
        }
      }
      controller.setSnapshot(await backend.request<ManagerSnapshot>('load', {}));
      controller.setConnection('ready');
      if (initialAdoption.descriptor) {
        const result = await backend.request<OperationResult>(
          'adoptStartupContinuation',
          {},
          150000,
        );
        adoptionPending = startupAdoptionBlocksInteraction(result);
        controller.setAdministrationPending(adoptionPending);
        if (!lifecycle.quietStartup) show();
        if (window && !lifecycle.quietStartup)
          await dialog.showMessageBox(window, {
            type: result.outcome === 'Confirmed' ? 'info' : 'warning',
            title: t.loginResultTitle,
            message: t.loginResultTitle,
            detail: result.detail ?? t.loginFailure(result.code),
            buttons: [t.confirm],
            defaultId: 0,
            cancelId: 0,
            noLink: true,
          });
      }
    } catch (error) {
      recordFailure('startBackend', error);
      controller.setConnection('failed', safeError(error));
      if (!lifecycle.quietStartup) show();
    }
  }
  function registerPortableApplication(owner: BackendClient): void {
    if (!app.isPackaged || process.platform !== 'win32' || deployment.mode !== 'portable') return;
    let appDataDirectory: string;
    try {
      appDataDirectory = app.getPath('appData');
    } catch {
      return;
    }
    void trace('lifecycle', 'repairSearchShortcut', () =>
      ensurePortableSearchShortcut(deployment, {
        platform: process.platform,
        appDataDirectory,
        permitted: () => {
          const state = controller?.state;
          return (
            backend === owner &&
            owner.ready &&
            state?.connection === 'ready' &&
            state.snapshot?.readOnly === false &&
            state.snapshot.effectsAvailable &&
            !state.administrationPending &&
            !adoptionPending &&
            !administration?.active &&
            !updateApplying &&
            !updateShutdownInFlight &&
            !stopExit.active &&
            !storageRestarting &&
            !quitting
          );
        },
        read: (file) => shell.readShortcutLink(file),
        write: (file, operation, details) => shell.writeShortcutLink(file, operation, details),
        diagnostic: ({ phase, code, attempt, retrying }) => {
          void trace(
            'lifecycle',
            'repairSearchShortcut.' + phase,
            () => {
              throw Object.assign(new Error('Search shortcut operation failed.'), { code });
            },
            { attempt, retrying },
          ).catch(() => {});
        },
      }),
    )
      .then((result) => diagnostics?.record('portable-search-shortcut', { result }))
      .catch(() => {});
  }
  async function reportLoginResult(result: OperationResult): Promise<void> {
    try {
      continuationOffer = backend?.ready
        ? await backend.request<StartupContinuationOffer | null>('startupContinuation', {})
        : null;
    } catch {
      continuationOffer = null;
    }
    updateTray();
    await reportStartupWithContinuation(
      result,
      continuationOffer?.canResume === true &&
        (!lifecycle.quietStartup || configuredLoginElevation(controller?.state.snapshot?.settings)),
      () => requestStartupContinuation(lifecycle.quietStartup),
      {
        show,
        warn: async (value) => {
          if (window)
            await dialog.showMessageBox(window, {
              type: 'warning',
              title: t.loginResultTitle,
              message: t.loginResultTitle,
              detail: value.detail ?? t.loginFailure(value.code),
              buttons: [t.confirm],
              defaultId: 0,
              cancelId: 0,
              noLink: true,
            });
        },
      },
      lifecycle.quietStartup,
    );
  }
  async function checkStartupAdoption(): Promise<void> {
    if (!adoptionPending || !backend?.ready || !controller || quitting) return;
    try {
      const result = await backend.request<OperationResult>('startupAdoptionStatus', {}, 10000);
      adoptionPending = startupAdoptionBlocksInteraction(result);
      controller.setAdministrationPending(adoptionPending);
      controller.setConnection('ready');
      if (backend) registerPortableApplication(backend);
      updateTray();
      show();
      if (window)
        await dialog.showMessageBox(window, {
          type: result.outcome === 'Confirmed' ? 'info' : 'warning',
          title: t.loginResultTitle,
          message: t.loginResultTitle,
          detail: result.detail ?? t.loginFailure(result.code),
          buttons: [t.confirm],
          defaultId: 0,
          cancelId: 0,
          noLink: true,
        });
    } catch {
      show();
      dialog.showErrorBox(t.loginResultTitle, t.administrationPending);
    }
  }
  async function requestStartupContinuation(quiet = false): Promise<boolean> {
    if (adoptionPending || administration?.active || !backend?.ready || !controller || quitting)
      return false;
    if (quiet && !lifecycle.takeAutomaticElevation(controller.state.snapshot?.settings))
      return false;
    continuationOffer = await backend.request<StartupContinuationOffer | null>(
      'startupContinuation',
      {},
    );
    updateTray();
    if (!continuationOffer?.canResume || !window) return false;
    if (!quiet) {
      show();
      const choice = await dialog.showMessageBox(window, {
        type: 'warning',
        title: t.continuationTitle,
        message: t.continuationTitle,
        detail: t.continuationDetail(continuationOffer.scriptNames),
        buttons: [t.continuationContinue, t.continuationLater],
        defaultId: 1,
        cancelId: 1,
        noLink: true,
      });
      if (choice.response !== 0) return false;
    }
    const deadline = Date.now() + 3000;
    while (!documentLease && !quitting && Date.now() < deadline)
      await new Promise((resolve) => setTimeout(resolve, 25));
    administrationLease = documentLease;
    let result: OperationResult | undefined;
    quietStartupTransfer = quiet;
    try {
      result = await administration?.request(true, continuationOffer.operationId, false, quiet);
    } finally {
      quietStartupTransfer = false;
    }
    if (result && result.outcome !== 'Confirmed' && window && !quiet)
      await dialog.showMessageBox(window, {
        type: 'warning',
        title: t.loginResultTitle,
        message: t.loginResultTitle,
        detail: result.detail ?? t.loginFailure(result.code),
        buttons: [t.confirm],
        defaultId: 0,
        cancelId: 0,
        noLink: true,
      });
    return result?.outcome === 'Confirmed';
  }
  async function runLogin(): Promise<void> {
    if (
      initialAdoption.descriptor !== null ||
      adoptionPending ||
      administration?.active ||
      stopExit.active ||
      quitting ||
      !backend?.ready ||
      !controller ||
      !lifecycle.takeLogin()
    )
      return;
    try {
      await reportLoginResult(await controller.runLogin());
    } catch (error) {
      recordFailure('runLogin', error);
      controller.setConnection('failed', safeError(error));
      if (!lifecycle.quietStartup) {
        show();
        dialog.showErrorBox(t.loginResultTitle, safeError(error));
      }
    }
  }
  async function requestAdministration(explicitCli = false, resumeLogin = false, quiet = false) {
    if (adoptionPending)
      return {
        outcome: 'CancelledBeforeDispatch' as const,
        code: 'PendingOperation',
        detail: null,
        correlationId: null,
      };
    if (administration?.processing)
      return {
        outcome: 'CancelledBeforeDispatch' as const,
        code: 'PendingOperation',
        detail: null,
        correlationId: null,
      };
    if (controller?.state.authorityMode === 'Administrator') {
      if (!quiet) show();
      return { outcome: 'Confirmed' as const, code: 'None', detail: null, correlationId: null };
    }
    if (explicitCli) {
      const deadline = Date.now() + 3000;
      while (!documentLease && !quitting && Date.now() < deadline)
        await new Promise((resolve) => setTimeout(resolve, 25));
    }
    administrationLease = documentLease;
    quietStartupTransfer = quiet;
    try {
      return (
        (await administration?.request(explicitCli, undefined, resumeLogin)) ?? {
          outcome: 'CancelledBeforeDispatch' as const,
          code: 'CapabilityUnverified',
          detail: null,
          correlationId: null,
        }
      );
    } finally {
      quietStartupTransfer = false;
    }
  }
  function createTray(): void {
    try {
      tray = createTrayIcon(() => {
        void trace('tray', 'activate', () => {
          lifecycle.requestExplicitActivation();
          show();
        });
      });
      trayRegistered = tray !== null;
      updateTray();
    } catch {
      trayRegistered = false;
      tray = null;
    }
  }
  function updateTray(): void {
    if (!tray || tray.isDestroyed()) return;
    tray.setContextMenu(
      Menu.buildFromTemplate(
        trayMenu(
          {
            adoptionPending,
            backendReady: backend?.ready === true,
            quitting,
            continuationOffer,
            administrationActive: administration?.active === true,
            windowVisible: window?.isVisible() === true,
            runningNames:
              controller?.state.snapshot?.scripts
                .filter((script) => script.status === 'Running')
                .map((script) => script.friendlyName) ?? [],
            stopExitActive: stopExit.active,
            connectionReady: controller?.state.connection === 'ready',
            progress: stopExitProgress,
          },
          {
            checkAdoption: trayAction('checkAdoption', checkStartupAdoption),
            continueStartup: trayAction('continueStartup', requestStartupContinuation),
            activate: trayAction('activate', () => {
              lifecycle.requestExplicitActivation();
              show();
            }),
            toggleWindow: trayAction('toggleWindow', () => {
              lifecycle.requestExplicitActivation();
              if (window?.isVisible()) window.hide();
              else show();
              updateTray();
            }),
            preferences: trayAction('preferences', showPreferences),
            quit: trayAction('quit', quit),
            stopScriptsAndQuit: trayAction('stopScriptsAndQuit', stopScriptsAndQuit),
            cancelStopExit: trayAction('cancelStopExit', () => {
              stopExit.cancel();
              updateTray();
            }),
          },
        ),
      ),
    );
  }
  function trayAction(command: string, action: () => unknown): () => void {
    return () => {
      void trace('tray', command, action).catch((error) => {
        if (!quitting) dialog.showErrorBox(t.title, safeError(error));
      });
    };
  }
  async function stopScriptsAndQuit(): Promise<void> {
    if (adoptionPending || administration?.active || quitting || !controller || !stopExit.begin())
      return;
    stopExitProgress = { done: 0, total: 0 };
    updateTray();
    try {
      const receipt = await controller.stopScriptsForExit({
        cancelled: () => stopExit.cancelled,
        progress: (done, total) => {
          stopExitProgress = { done, total };
          updateTray();
        },
      });
      if (stopExit.complete(receipt.canExit)) {
        await quit();
        return;
      }
      updateTray();
      show();
      if (window)
        await dialog.showMessageBox(window, {
          type: 'warning',
          title: t.stopExitPartialTitle,
          message: t.stopExitPartialTitle,
          detail: t.stopExitPartial(receipt),
          buttons: [t.confirm],
          defaultId: 0,
          cancelId: 0,
          noLink: true,
        });
    } catch (error) {
      recordFailure('stopScriptsAndQuit', error);
      stopExit.complete(false);
      updateTray();
      show();
      dialog.showErrorBox(t.stopExitPartialTitle, safeError(error));
    }
  }
  async function closeWindow(): Promise<void> {
    if (updateApplying || updateShutdownInFlight || dataLocation?.active) {
      show();
      return;
    }
    if (administration?.active) {
      show();
      return;
    }
    if (stopExit.active) {
      stopExit.cancel();
      updateTray();
      return;
    }
    if (quitting) return;
    try {
      await controller?.savePlacement();
    } catch (error) {
      controller?.setConnection('failed', safeError(error));
    }
    if (
      lifecycle.closeAction(
        controller?.state.snapshot?.settings.closeBehavior ?? 'Exit',
        trayRegistered,
      ) === 'hide'
    ) {
      window?.hide();
      updateTray();
    } else await quit();
  }
  async function quit(administrativeAcknowledgement = false): Promise<void> {
    if ((updateApplying || updateShutdownInFlight || dataLocation?.active) && !quitComplete) {
      show();
      return;
    }
    if (administration?.active && !administrativeAcknowledgement) {
      show();
      return;
    }
    if (stopExit.active) {
      stopExit.cancel();
      updateTray();
      return;
    }
    if (quitting || quitComplete) return;
    quitting = true;
    controller?.setConnection('closing');
    let timer: ReturnType<typeof setTimeout> | undefined;
    const orderly = () =>
      orderlyShutdown({
        drain: async () => {
          await controller?.drain();
        },
        backendPresent: () => backend !== null,
        backendExited: () => backend?.exited ?? true,
        persistPlacement: async () => {
          if (!administrativeAcknowledgement) await controller?.savePlacement();
        },
        shutdownBackend: async () => {
          if (backend) await backend.shutdown();
        },
        reportPlacementFailure: (error) => controller?.setConnection('closing', safeError(error)),
      });
    try {
      await trace('lifecycle', 'shutdown', () =>
        Promise.race([
          orderly(),
          new Promise<never>((_resolve, reject) => {
            timer = setTimeout(
              () => reject(new Error(t.shutdownDeadline(backend?.pid ?? null))),
              SHUTDOWN_DEADLINE_MS,
            );
          }),
        ]),
      );
      tray?.destroy();
      quitComplete = true;
      app.quit();
    } catch (error) {
      quitting = false;
      controller?.setConnection('failed', safeError(error));
      if (!quietStartupTransfer) {
        show();
        dialog.showErrorBox(t.shutdownUnconfirmed, safeError(error));
      }
    } finally {
      clearTimeout(timer);
    }
  }
  function registerIpc(): void {
    if (!guard || !controller || !updates) throw new Error('IPC setup unavailable');
    registerHandlers({
      guard,
      controller,
      updates,
      preflight,
      documentScope,
      theme,
      ipc: ipcMain,
      clipboard,
      commandLog: commandTrace,
      openLogs: async () => {
        if (!commandLog?.status().available)
          throw new Error('Diagnostic logging is unavailable. Check access to the data folder.');
        const error = await shell.openPath(commandLog.directory);
        if (error) throw new Error(error);
      },
      storage: dataLocation
        ? {
            get: () => dataLocation!.state,
            choose: dataLocation.choose,
            open: dataLocation.open,
            restoreDefault: dataLocation.restoreDefault,
            restart: dataLocation.restart,
            cancel: dataLocation.cancel,
          }
        : undefined,
      policy: () => ({
        quitting,
        updateApplying: updateApplying || dataLocation?.active === true,
        adoptionPending,
        administrationPending: controller!.state.administrationPending,
      }),
      requestAdministration,
      retryBackend: async () => {
        if (updateShutdownInFlight) throw new Error('Wait for confirmed backend shutdown.');
        await startBackend();
        if (backend?.ready) await runLogin();
        if (backend) registerPortableApplication(backend);
      },
      selectionChanged: (selected) => {
        diagnosticSelection = selected?.scriptId ?? null;
        diagnostics?.selectionCommitted(diagnosticSelection);
      },
      documentReady: (lease) => {
        documentLease = lease;
        rendererReady = true;
        deliverPreferences();
      },
    });
  }
}
