import type { StopExitReceipt } from './controller/stop-exit.js';
import type { UpdateRelease } from '../shared/updates.js';
import { AgentReceipts } from './agent-receipts.js';
import { AgentTransitionCoordinator } from './agent-transition-coordinator.js';
import { AgentCommandError, AgentCommands, failed, ok, operationReply } from './agent-commands.js';
import { startAgentTransport, type AgentReply } from './agent-transport.js';
import { CommandRegistry } from './command-registry.js';
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
import { release as operatingSystemRelease } from 'node:os';
import { BackendClient } from './backend.js';
import { ManagerController } from './controller.js';
import {
  administrationCli,
  startupAdoptionCli,
  startupAdoptionBlocksInteraction,
  LifecycleIntent,
  StopExitIntent,
  SHUTDOWN_DEADLINE_MS,
  shutdownWithinDeadline,
  reportStartupWithContinuation,
  configuredLoginElevation,
  desktopPreflight,
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
import { unknownValue } from '../shared/validation.js';
import type { UpdateService } from './updates/update-service.js';
import type { UpdateStore, CachedPackage } from './updates/update-store.js';
import { compareVersions } from './updates/github-updates.js';
import { prepareUpdate, type PreparedUpdate } from './updates/update-handoff.js';
import { shutdownAndCommitUpdate } from './updates/update-shutdown.js';

import { serveAssets, lockSession } from './desktop/assets.js';
import { createMainWindow } from './desktop/main-window.js';
import { ShutdownCoordinator } from './desktop/shutdown-coordinator.js';
import { resolveStartupDeployment } from './desktop/logon-deployment.js';
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
import {
  assertStoragePath,
  resolveDeployment,
  samePath,
  type DeploymentLayout,
} from './storage/deployment.js';
import {
  canKeepCurrentData,
  completePendingDataMove,
  DataMovePreparationCancelled,
  importLegacyUpdateChannel,
  pendingMoveIdentity,
} from './storage/data-migration.js';
import { withDataMoveLease } from './storage/data-move-lease.js';
import {
  commandDetails,
  type CommandLog,
  type CommandLogBoundary,
  type CommandDetails,
  type CommandSource,
  type DiagnosticLogMode,
} from './logging/command-log.js';
import { createCommandLog } from './logging/create-command-log.js';
import { recordStartupFailure, startupFailureDirectory } from './logging/startup-failure.js';
import {
  createDiagnosticReport,
  createDiagnosticRuntime,
  type DiagnosticReportContext,
} from './logging/diagnostic-report.js';
import { createDiagnosticArchive } from './logging/diagnostic-archive.js';
import {
  diagnosticDestinationStamp,
  exportDiagnosticArchive,
  writeDiagnosticArchive,
} from './logging/diagnostic-export.js';
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
  const agentHost = process.argv.includes('--agent-host');
  const generation = randomUUID();
  const commands = new CommandRegistry();
  let agentCommands: AgentCommands | null = null;
  let closeAgent: (() => Promise<void>) | null = null;
  let desktopReady = false;
  let explicitWindowPending = false;
  let windowLoading: Promise<void> | null = null;
  let cliExitPending = false;
  let deployment: DeploymentLayout;
  let bootstrapLayout: DeploymentLayout | null = null;
  let storageError: string | null = null;
  let storageRestarting = false;
  try {
    deployment = resolveStartupDeployment(
      app.getPath('exe'),
      localAppData,
      process.argv,
      app.isPackaged,
    );
    bootstrapLayout = deployment;
    initializePortableStorage(deployment);
  } catch (error) {
    const quiet =
      !process.argv.includes('--foreground') &&
      !process.argv.includes('--request-administrator') &&
      (agentHost || process.argv.includes('--login') || process.argv.includes('--background'));
    reportStartupFailure(error, quiet);
    return;
  }
  // Chromium storage never opens or copies a framework's previous operational profile.
  app.setPath('userData', deployment.chromiumDirectory);
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
    !agentHost &&
      !adoptionPending &&
      initialAdministration === 'none' &&
      process.argv.includes('--login'),
    (adoptionPending && !quietAdoption) ||
      initialAdministration !== 'none' ||
      process.argv.includes('--foreground'),
    quietAdoption || agentHost,
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
  let dataLocation: DataLocationController | null = null;
  let storageLease: PortableStorageLease | null = null;
  let storageClosing: Promise<void> | null = null;
  let storageClosed = false;
  let commandLog: CommandLog | null = null;
  const commandTrace: Pick<CommandLogBoundary, 'run'> = { run: trace };
  const documentScope = new AsyncLocalStorage<() => boolean>();
  const trayScope = new AsyncLocalStorage<() => boolean>();
  const agentScope = () => agentCommands?.scope.getStore();
  const alive = () =>
    !quitting &&
    (agentScope()?.alive() ??
      documentScope.getStore()?.() ??
      trayScope.getStore()?.() ??
      (!!window && !window.isDestroyed()));
  const persistWindow = async (administrative = false) => {
    if (window && !window.isDestroyed()) await controller?.savePlacement(administrative);
  };
  const shutdown = new ShutdownCoordinator({
    backend: () => backend,
    drain: async () => {
      await controller?.drain();
    },
    persistPlacement: persistWindow,
  });
  const transitions = new AgentTransitionCoordinator({
    generation,
    scope: agentScope,
    ready: () =>
      hostReady() &&
      !cliExitPending &&
      !controller?.state.busy &&
      !updates?.busy &&
      !shutdown.inFlight &&
      !updateApplying &&
      !adoptionPending &&
      !administration?.active &&
      !stopExit.active &&
      !dataLocation?.active,
    owner: () => backend,
    current: (owner) => !quitting && backend === owner,
    receipts: () => (updateStore ? new AgentReceipts(updateStore) : null),
    review: (operationId, eligible) => reviewDesktop(operationId, 'begin', eligible),
    run: (scope, work) => agentCommands!.scope.run(scope, work),
    uncertain: () => !backend?.ready || quitting,
    error: safeError,
    report: (error) => recordFailure('agentReceipt', error),
    resume: startAgent,
  });
  const desktopTheme = new DesktopTheme(
    () => window,
    () => controller?.state.snapshot?.settings,
    (state) => guard?.send('manager:theme', state),
  );
  const theme = (): ThemeState => desktopTheme.state;
  const updateTheme = desktopTheme.update;
  const nativeDialogs = createNativeDialogs(() => window, alive);
  const pick: typeof nativeDialogs.files = (kind) =>
    trace('ui', 'pick.' + kind, () => agentScope()?.files ?? nativeDialogs.files(kind));
  const confirm: typeof nativeDialogs.confirm = (title, detail, target) =>
    trace('ui', 'confirm', async () => {
      const scope = agentScope();
      if (scope) return scope.confirm(title, detail, target);
      const fromTray = trayScope.getStore();
      if (!fromTray) return nativeDialogs.confirm(title, detail);
      if (!fromTray() || (!window?.isVisible() && !(await showWindow()))) return false;
      const owner = window;
      const confirmed = await nativeDialogs.confirm(title, detail);
      return confirmed && window === owner && !!owner && !owner.isDestroyed();
    });
  const single = app.requestSingleInstanceLock();
  if (!single) app.quit();
  else {
    app.on('second-instance', (_event, argv) => {
      void trace('lifecycle', 'secondInstance', () => {
        if (argv.includes('--agent-host')) return;
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
      if (!quitting && !agentHost) void quit();
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
        quitComplete = true;
        reportStartupFailure(error, agentHost || lifecycle.quietStartup, () => app.quit());
      });
  }
  function reportStartupFailure(error: unknown, quiet: boolean, finish = () => app.exit(65)): void {
    const report = recordStartupFailure({
      localAppDataDirectory: localAppData,
      version: app.getVersion(),
      executablePath: app.getPath('exe'),
      layout: bootstrapLayout,
      login: process.argv.includes('--login'),
      background: process.argv.includes('--background'),
      error,
    });
    if (quiet) {
      finish();
      return;
    }
    void app
      .whenReady()
      .then(async () => {
        const buttons = [
          'Copy diagnostic details',
          ...(report.directory ? ['Open diagnostics folder'] : []),
          'Close',
        ];
        const result = await dialog.showMessageBox({
          type: 'error',
          title: t.title,
          message: safeError(error),
          detail:
            report.summary +
            '\n\n' +
            (report.file
              ? `Diagnostic details were saved to:\n${report.file}\n\nCopy the details to share them for troubleshooting.`
              : 'The diagnostic file could not be saved. You can copy the details for troubleshooting.'),
          buttons,
          defaultId: buttons.length - 1,
          cancelId: buttons.length - 1,
          noLink: true,
        });
        if (result.response === 0) clipboard.writeText(report.text);
        else if (result.response === 1 && report.directory) {
          assertStoragePath(report.directory);
          const directory = await fs.lstat(report.directory);
          if (directory.isDirectory() && !directory.isSymbolicLink())
            await shell.openPath(report.directory);
        }
      })
      .catch(() => {
        /* The report is already retained when native error UI is unavailable. */
      })
      .finally(finish);
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
      let dataMoveFailure: { error: unknown } | null = null;
      try {
        deployment = await withDataMoveLease(previous, (boundary) =>
          completePendingDataMove(previous, boundary),
        );
      } catch (error) {
        const fallback = resolveDeployment(app.getPath('exe'), localAppData);
        if (!(await canKeepCurrentData(fallback))) throw error;
        deployment = fallback;
        storageError = safeError(error);
        dataMoveFailure = { error };
      }
      if (!samePath(previous.dataDirectory, deployment.dataDirectory)) {
        // Electron may have initialized Chromium while the asynchronous copy ran.
        // A fresh process selects the committed data path before Chromium starts.
        app.relaunch({
          execPath: deployment.launchExecutable,
          args: [agentHost ? '--agent-host' : '--foreground'],
        });
        storageRestarting = true;
        quitComplete = true;
        app.exit(0);
        return;
      }
      // Check the authoritative pointer again after acquiring the selected lock.
      const current = resolveDeployment(app.getPath('exe'), localAppData);
      if (!samePath(current.dataDirectory, deployment.dataDirectory))
        throw new Error('The data location changed during startup. Open Manager again.');
      storageLease = await acquirePortableStorageLease(deployment, (error) => {
        // Losing namespace protection must stop writes and cannot authorize an update.
        recordFailure('storageLease', error);
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
      commandLog = createCommandLog(deployment);
      if (dataMoveFailure) recordFailure('storage:restart', dataMoveFailure.error);
      process.on('uncaughtExceptionMonitor', (error) => recordFailure('uncaughtException', error));
    } catch (error) {
      storageError = safeError(error);
      throw error;
    }
  }
  function show(): void {
    if (!desktopReady) explicitWindowPending = true;
    void showWindow().catch((error) => recordFailure('showWindow', error));
  }
  async function showWindow(): Promise<boolean> {
    if (!desktopReady || quitting) return false;
    await ensureWindow();
    if (!window || window.isDestroyed() || quitting) return false;
    lifecycle.showWindow(window);
    deliverPreferences();
    updateTray();
    return window.isVisible();
  }
  function deliverPreferences(): void {
    if (lifecycle.takePreferences(ready && rendererReady && !!guard?.connected))
      guard?.send('manager:preferences', null);
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
  function reviewDesktop(
    operationId: string,
    phase: 'begin' | 'commit',
    eligible: () => boolean,
  ): Promise<boolean> {
    const agent = agentScope();
    const owner = window;
    const lease = documentLease;
    return desktopPreflight(
      eligible,
      agent?.alive,
      owner && !owner.isDestroyed()
        ? {
            alive: () => window === owner && !owner.isDestroyed(),
            focused: () => owner.isFocused(),
            documentCurrent: () => !!lease && !!guard?.current(lease),
            review: (valid) =>
              preflight.check(
                operationId,
                phase,
                (request) => guard?.send('manager:administrationPreflight', request),
                valid,
              ),
          }
        : undefined,
    );
  }
  async function configureUpdates(): Promise<void> {
    const configured = await createApplicationUpdates(
      {
        preflight: (operationId, phase) => {
          if (dataLocation?.state.pendingDataDirectory)
            throw new Error('Restart Manager to complete the data move before updating.');
          return reviewDesktop(
            operationId,
            phase,
            () =>
              !quitting &&
              !updateApplying &&
              !dataLocation?.active &&
              !shutdown.inFlight &&
              !adoptionPending &&
              !administration?.active &&
              !stopExit.active &&
              !cliExitPending &&
              !controller?.state.busy &&
              !controller?.state.administrationPending &&
              (!transitions.active || transitions.active === operationId),
          );
        },
        confirm: (release) => {
          const agent = agentScope();
          if (agent)
            return Promise.resolve(agent.alive() && agent.updateRelease === releaseKey(release));
          return confirm(
            compareVersions(release.version, app.getVersion()) < 0
              ? 'Return to version ' + release.version + '?'
              : 'Update to version ' + release.version + '?',
            'The manager will close and reopen. Running scripts will keep running, and your settings will be preserved.',
          );
        },
        apply: applyUpdate,
        changed: (state) => guard?.send('manager:updates', state),
      },
      () =>
        !quitting &&
        !updateApplying &&
        !transitions.active &&
        !dataLocation?.active &&
        (!agentHost || !!window),
      {
        deployment: deployment.mode,
        checkOnStart: !agentHost,
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
    const operation = shutdown.operation('administrative');
    const check = (phase: 'begin' | 'commit') =>
      reviewDesktop(
        operationId,
        phase,
        () =>
          !quitting &&
          !updateApplying &&
          !shutdown.inFlight &&
          !adoptionPending &&
          !administration?.active &&
          !stopExit.active &&
          !cliExitPending &&
          !updates?.busy &&
          dataLocation?.active === true &&
          controller?.state.busy === false,
      );
    try {
      if (!(await check('begin')))
        throw new Error(
          'Save or discard your changes and wait for operations to finish before restarting Manager.',
        );
      await shutdownWithinDeadline(operation.boundary, SHUTDOWN_DEADLINE_MS, {
        beforeShutdown: async () => {
          if (!(await check('commit')))
            throw new Error('The restart was cancelled because the application state changed.');
        },
        cancelled: 'The data location restart was cancelled.',
        timedOut: 'Manager shutdown was not confirmed in time. The data location was preserved.',
        unconfirmed: 'Manager shutdown was not confirmed. The data location was preserved.',
      });
      await shutdownAgent();
      app.relaunch({
        execPath: deployment.launchExecutable,
        args: [agentScope() || agentHost ? '--agent-host' : '--foreground'],
      });
      quitting = true;
      tray?.destroy();
      quitComplete = true;
      app.quit();
    } catch (error) {
      if (operation.dispatched || backend?.exited)
        controller?.setConnection('failed', safeError(error));
      if (!agentScope()) {
        show();
        dialog.showErrorBox('Data location', safeError(error));
      }
      throw error;
    }
  }
  async function applyUpdate(operationId: string, target: CachedPackage): Promise<void> {
    if (
      !updateStore ||
      (!window && !agentScope()) ||
      updateApplying ||
      shutdown.inFlight ||
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
      await verifyCachedPortablePackageSignature(target.archivePath);
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
        agentScope() !== undefined,
      );
      shutdownStarted = true;
      await shutdownAndCommitUpdate(
        shutdown.operation('administrative').boundary,
        SHUTDOWN_DEADLINE_MS,
        async () => {
          // The verifier and the Electron host must release all images in App before replacement.
          await shutdownAgent();
          await prepared!.commit();
        },
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
  async function ensureWindow(): Promise<void> {
    if (windowLoading) return windowLoading;
    if (window && !window.isDestroyed()) return;
    windowLoading = openWindow();
    try {
      await windowLoading;
    } finally {
      windowLoading = null;
    }
  }
  async function openWindow(): Promise<void> {
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
    applySavedPlacement();
    await window.loadURL(APP_URL);
    if (window.isVisible()) diagnostics?.initialReady();
  }
  function applySavedPlacement(): void {
    const saved = controller?.state.snapshot?.settings.window;
    if (!saved || !window || window.isDestroyed()) return;
    const dip = screen.screenToDipRect(window, {
      x: saved.x,
      y: saved.y,
      width: saved.width,
      height: saved.height,
    });
    window.setBounds(restorePlacement({ ...saved, ...dip, dpi: 96 }, screen.getAllDisplays()));
    lifecycle.deferMaximizedRestore(saved.maximized);
  }
  async function startApplication(): Promise<void> {
    if (
      initialAdoption.invalid ||
      (agentHost &&
        (adoptionPending || initialAdministration !== 'none' || process.argv.includes('--login')))
    )
      throw new Error(t.adoptionInvalid);
    if (process.platform !== 'win32' || !app.isPackaged) throw new Error(t.windowsPackagedRequired);
    await serveAssets();
    lockSession();
    Menu.setApplicationMenu(null);
    desktopReady = true;
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
        await persistWindow(true);
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
        !shutdown.inFlight &&
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
    registerCommands();
    if (!agentHost) await ensureWindow();
    createTray();
    updateTheme();
    await startBackend();
    applySavedPlacement();
    if (window?.isVisible()) await showWindow();
    ready = true;
    if (agentHost) {
      if (explicitWindowPending) await showWindow();
      return;
    }
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
    if (window?.isVisible()) diagnostics?.initialReady();
  }
  async function startBackend(): Promise<void> {
    if (administration?.active) throw new Error(t.administrationPending);
    if (stopExit.active) throw new Error(t.stopExitBusy);
    if (quitting) throw new Error(t.closing);
    if (!controller) throw new Error('Controller unavailable');
    if (backend && !backend.exited) throw new Error(t.backendStillAlive(backend.pid));
    agentCommands?.invalidate();
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
      if (!quitting) {
        agentCommands?.invalidate();
        controller?.setConnection('failed', error.message);
      }
    });
    try {
      await trace('backend', 'start', () => backend!.start());
      controller.setHostAuthority(backend.authorityMode);
      const initialized = await backend.request<OperationResult>('initializeProfile', {});
      if (initialized.outcome !== 'Confirmed')
        throw new Error(initialized.detail ?? 'The application profile could not be prepared.');
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
      if (!agentHost && !lifecycle.quietStartup) show();
    }
  }
  function registerPortableApplication(owner: BackendClient): void {
    if (!app.isPackaged || process.platform !== 'win32') return;
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
            !shutdown.inFlight &&
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
  async function checkStartupAdoption(quiet = false): Promise<OperationResult | null> {
    if (!adoptionPending || !backend?.ready || !controller || quitting) return null;
    try {
      const result = await backend.request<OperationResult>('startupAdoptionStatus', {}, 10000);
      adoptionPending = startupAdoptionBlocksInteraction(result);
      controller.setAdministrationPending(adoptionPending);
      controller.setConnection('ready');
      if (backend) registerPortableApplication(backend);
      updateTray();
      if (!quiet) show();
      if (window && !quiet)
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
      return result;
    } catch (error) {
      if (quiet) throw error;
      show();
      dialog.showErrorBox(t.loginResultTitle, t.administrationPending);
      return null;
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
  function startAgent(): void {
    if (closeAgent || !agentCommands || !app.isPackaged || process.platform !== 'win32') return;
    closeAgent = startAgentTransport(
      path.join(path.dirname(app.getPath('exe')), 'backend', 'agent', 'ahk-manager.exe'),
      { version: app.getVersion(), generation },
      (request, live) =>
        trace('agent', 'agent:' + request.command, () => agentCommands!.dispatch(request, live), {
          ...commandDetails(request.arguments),
          agentRequestId: request.requestId,
        }),
      (error) => recordFailure('agentChild', error),
    );
  }
  async function shutdownAgent(): Promise<void> {
    const close = closeAgent;
    if (!close) return;
    await close();
    if (closeAgent === close) closeAgent = null;
  }
  function hostReady(): boolean {
    return (
      ready && backend?.ready === true && controller?.state.connection === 'ready' && !quitting
    );
  }
  function agentStatus() {
    return {
      ready: hostReady(),
      connection: controller?.state.connection ?? 'starting',
      authorityMode: controller?.state.authorityMode ?? null,
      windowOpen: !!window && !window.isDestroyed(),
      windowVisible: window?.isVisible() === true,
      stopExit: { active: stopExit.active, ...stopExitProgress },
      generation,
    };
  }
  async function navigate(
    command: 'show' | 'hide' | 'toggleWindow' | 'preferences',
  ): Promise<AgentReply> {
    if (quitting || cliExitPending || updateApplying || shutdown.inFlight || dataLocation?.active)
      return failed('OperationBlocked');
    if (command === 'hide' || (command === 'toggleWindow' && window?.isVisible())) {
      if (window?.isVisible() && !trayRegistered) return failed('TrayUnavailable');
      window?.hide();
    } else {
      if (command === 'preferences') {
        if (administration?.active) return failed('OperationBlocked');
        lifecycle.requestPreferences();
      } else lifecycle.requestExplicitActivation();
      if (!(await showWindow())) return failed('WindowUnavailable');
    }
    updateTray();
    return ok(agentStatus());
  }
  function cancelStop(): AgentReply {
    if (!stopExit.active) return failed('OperationNotFound');
    stopExit.cancel();
    updateTray();
    return { outcome: 'Accepted', code: 'CancellationAccepted', result: stopExitProgress };
  }
  async function requestAgentExit(stopScripts: boolean): Promise<AgentReply> {
    if (
      quitting ||
      cliExitPending ||
      transitions.active ||
      updateApplying ||
      shutdown.inFlight ||
      dataLocation?.active ||
      updates?.busy ||
      administration?.active ||
      adoptionPending ||
      !ready
    )
      return failed('OperationBlocked');
    let receipt: StopExitReceipt | null = null;
    if (stopScripts) {
      receipt = await runStopExit(true);
      if (!receipt) return failed('OperationInProgress');
      if (!receipt.canExit) {
        if (
          receipt.cancelled &&
          !receipt.confirmedStops &&
          !receipt.items.some((item) => item.outcome === 'unknown')
        )
          return failed('CancelledBeforeDispatch', receipt);
        const uncertain = receipt.items.some((item) => item.outcome === 'unknown');
        return {
          outcome: receipt.confirmedStops ? 'Partial' : uncertain ? 'Unknown' : 'Failed',
          code: 'StopExitIncomplete',
          result: receipt,
        };
      }
    } else if (stopExit.active) return failed('OperationInProgress');
    if (
      quitting ||
      cliExitPending ||
      transitions.active ||
      updateApplying ||
      shutdown.inFlight ||
      dataLocation?.active ||
      updates?.busy ||
      administration?.active ||
      adoptionPending
    )
      return receipt
        ? { outcome: 'Partial', code: 'ShutdownBlocked', result: receipt }
        : failed('OperationBlocked');
    cliExitPending = true;
    updateTray();
    return {
      outcome: 'Accepted',
      code: 'ShutdownAccepted',
      result: receipt,
      afterReply: async (delivered) => {
        cliExitPending = false;
        updateTray();
        if (delivered) await quit(false, true);
      },
    };
  }
  function releaseKey(release: UpdateRelease): string {
    return JSON.stringify([release.id, release.version, release.commit, release.publishedAt]);
  }
  async function requestAgentUpdate(id: string): Promise<AgentReply> {
    const choice = updates?.snapshot.releases.find(
      (release) => release.id === id && release.canInstall,
    );
    if (!choice) return failed('UpdateUnavailable');
    const key = releaseKey(choice);
    await confirm(
      'Install version ' + choice.version,
      'Manager will restart while scripts keep running.',
      key,
    );
    if (!sameRelease(choice)) return failed('ConfirmationChanged');
    const operationId = randomUUID();
    return transitions.reserve(
      operationId,
      { kind: 'Update', version: choice.version },
      async () => {
        if (!sameRelease(choice)) return 'Failed';
        const state = await updates!.install(id, operationId);
        if (quitting) return 'Dispatched';
        if (!backend?.ready) throw new Error(state.error ?? 'Update shutdown was unconfirmed.');
        if (state.downloadCancellation === 'canceled') return 'Cancelled';
        return 'Failed';
      },
      key,
    );
  }
  function sameRelease(choice: UpdateRelease): boolean {
    return (
      updates?.snapshot.releases.some(
        (release) => release.canInstall && releaseKey(release) === releaseKey(choice),
      ) === true
    );
  }
  async function requestAgentStorageRestart(): Promise<AgentReply> {
    const move = pendingMoveIdentity(deployment);
    if (!move || !dataLocation) return failed('OperationNotFound');
    await confirm('Restart to move data', move.destination, move);
    return transitions.reserve(
      randomUUID(),
      { kind: 'DataMove', moveId: move.operationId, destination: move.destination },
      async () => {
        const pending = pendingMoveIdentity(deployment);
        if (
          pending?.operationId !== move.operationId ||
          !samePath(pending.destination, move.destination)
        )
          return 'Failed';
        await dataLocation!.restart();
        return quitting ? 'Dispatched' : 'Failed';
      },
    );
  }
  function registerCommands(): void {
    if (!controller) throw new Error('Controller unavailable.');
    commands.define('tray.checkAdoption', unknownValue, () => checkStartupAdoption());
    commands.define('tray.continueStartup', unknownValue, () => requestStartupContinuation());
    commands.define('tray.activate', unknownValue, () => navigate('show'));
    commands.define('tray.toggleWindow', unknownValue, () => navigate('toggleWindow'));
    commands.define('tray.preferences', unknownValue, () => navigate('preferences'));
    commands.define('tray.quit', unknownValue, () => quit());
    commands.define('tray.stopScriptsAndQuit', unknownValue, () => stopScriptsAndQuit());
    commands.define('tray.cancelStopExit', unknownValue, cancelStop);
    agentCommands = new AgentCommands({
      registry: commands,
      controller,
      status: agentStatus,
      ready: hostReady,
      policy: requestPolicy,
      navigation: navigate,
      shutdown: requestAgentExit,
      cancelStopExit: cancelStop,
      checkAdoption: async () => {
        const result = await checkStartupAdoption(true);
        return result ? operationReply(result) : failed('OperationNotFound');
      },
      continuation: async () => {
        if (!backend?.ready)
          throw new AgentCommandError('BackendUnavailable', t.backendUnavailable);
        return backend.request<StartupContinuationOffer | null>('startupContinuation', {});
      },
      resumeStartup: async (operationId) => {
        if (!backend?.ready) return failed('BackendUnavailable');
        const offer = await backend.request<StartupContinuationOffer | null>(
          'startupContinuation',
          {},
        );
        if (!offer?.canResume || offer.operationId !== operationId)
          return failed('OperationNotFound');
        await confirm(t.continuationTitle, t.continuationDetail(offer.scriptNames), offer);
        // Startup adoption owns the original normal-to-administrator process handoff.
        return failed('StartupHandoffRequired');
      },
      update: requestAgentUpdate,
      cancelUpdate: (id) =>
        updates?.cancelDownload(id) === 'accepted'
          ? { outcome: 'Accepted', code: 'CancellationAccepted', result: { id } }
          : failed('OperationNotFound'),
      prepareStorage: async (directory) => {
        if (!dataLocation) throw new AgentCommandError('BackendUnavailable', t.backendUnavailable);
        try {
          return await (directory === undefined
            ? dataLocation.restoreDefault()
            : dataLocation.prepareExplicit(directory));
        } catch (error) {
          if (!(error instanceof DataMovePreparationCancelled)) throw error;
          throw new AgentCommandError(
            error.cleanupConfirmed ? 'CancelledBeforeDispatch' : 'StoragePreparationUnconfirmed',
            error.message,
            error.cleanupConfirmed ? 'Failed' : 'Unknown',
          );
        }
      },
      restartStorage: requestAgentStorageRestart,
      report: diagnosticReport,
      exportDiagnostics: async (destination) => {
        const stamp = diagnosticDestinationStamp(destination);
        await confirm('Export diagnostic archive', destination, { destination, stamp });
        return diagnosticExport(destination, stamp);
      },
      receipt: (operationId) =>
        updateStore
          ? new AgentReceipts(updateStore).reconcile(operationId, {
              generation,
              version: app.getVersion(),
              ready: hostReady(),
              directory: deployment.dataDirectory,
              active: transitions.active === operationId,
            })
          : Promise.resolve(failed('OperationNotFound')),
      error: safeError,
    });
    startAgent();
  }
  function createTray(): void {
    try {
      tray = createTrayIcon(trayAction('activate'));
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
            checkAdoption: trayAction('checkAdoption'),
            continueStartup: trayAction('continueStartup'),
            activate: trayAction('activate'),
            toggleWindow: trayAction('toggleWindow'),
            preferences: trayAction('preferences'),
            quit: trayAction('quit'),
            stopScriptsAndQuit: trayAction('stopScriptsAndQuit'),
            cancelStopExit: trayAction('cancelStopExit'),
          },
        ),
      ),
    );
  }
  function trayAction(command: string): () => void {
    return () => {
      const owner = tray;
      if (!owner || owner.isDestroyed()) return;
      let active = true;
      const valid = () => active && tray === owner && !owner.isDestroyed() && !quitting;
      void trayScope
        .run(valid, () => trace('tray', command, () => commands.invoke('tray.' + command, {})))
        .catch((error) => {
          if (!quitting) dialog.showErrorBox(t.title, safeError(error));
        })
        .finally(() => {
          active = false;
        });
    };
  }
  async function runStopExit(explicit: boolean): Promise<StopExitReceipt | null> {
    if (
      adoptionPending ||
      administration?.active ||
      quitting ||
      cliExitPending ||
      transitions.active ||
      updateApplying ||
      updates?.busy ||
      dataLocation?.active ||
      !controller ||
      !stopExit.begin()
    )
      return null;
    stopExitProgress = { done: 0, total: 0 };
    updateTray();
    try {
      const receipt = await controller.stopScriptsForExit({
        cancelled: () => stopExit.cancelled,
        explicit,
        progress: (done, total) => {
          stopExitProgress = { done, total };
          updateTray();
        },
      });
      receipt.canExit = stopExit.complete(receipt.canExit);
      return receipt;
    } finally {
      if (stopExit.active) stopExit.complete(false);
      updateTray();
    }
  }
  async function stopScriptsAndQuit(): Promise<void> {
    try {
      const receipt = await runStopExit(false);
      if (!receipt) return;
      if (receipt.canExit) {
        await quit();
        return;
      }
      if ((await showWindow()) && window)
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
      show();
      dialog.showErrorBox(t.stopExitPartialTitle, safeError(error));
    }
  }
  async function closeWindow(): Promise<void> {
    if (cliExitPending || transitions.active) return;
    if (updateApplying || shutdown.inFlight || dataLocation?.active) {
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
  async function quit(administrativeAcknowledgement = false, quiet = false): Promise<void> {
    if (cliExitPending || transitions.active) return;
    if ((updateApplying || shutdown.inFlight || dataLocation?.active) && !quitComplete) {
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
    const operation = shutdown.operation(
      administrativeAcknowledgement ? 'skip' : 'standard',
      (error) => controller?.setConnection('closing', safeError(error)),
    );
    try {
      await trace('lifecycle', 'shutdown', () =>
        shutdownWithinDeadline(operation.boundary, SHUTDOWN_DEADLINE_MS, {
          cancelled: t.closing,
          timedOut: t.shutdownDeadline(backend?.pid ?? null),
          unconfirmed: t.shutdownUnconfirmed,
        }),
      );
      await shutdownAgent();
      tray?.destroy();
      quitComplete = true;
      app.quit();
    } catch (error) {
      quitting = false;
      startAgent();
      controller?.setConnection('failed', safeError(error));
      if (!quiet && !quietStartupTransfer) {
        show();
        dialog.showErrorBox(t.shutdownUnconfirmed, safeError(error));
      }
    }
  }
  function diagnosticContext(): DiagnosticReportContext {
    return {
      version: app.getVersion(),
      platform: process.platform,
      architecture: process.arch,
      operatingSystemRelease: operatingSystemRelease(),
      deployment: deployment.mode,
      host: controller?.state ?? null,
      updates: updates?.snapshot ?? null,
    };
  }
  function diagnosticReport(mode: DiagnosticLogMode): string {
    return createDiagnosticReport(diagnosticContext(), commandLog, mode);
  }
  function diagnosticExport(destination?: string, expected?: string | null) {
    return exportDiagnosticArchive({
      chooseDestination: async () => {
        if (destination !== undefined) return destination;
        const options = {
          title: 'Save diagnostic archive',
          defaultPath: path.join(
            app.getPath('downloads'),
            `AHK-Manager-diagnostics-${new Date().toISOString().replace(/[:.]/g, '-')}.zip`,
          ),
          filters: [{ name: 'ZIP archives', extensions: ['zip'] }],
        };
        const result = window
          ? await dialog.showSaveDialog(window, options)
          : await dialog.showSaveDialog(options);
        return result.canceled || !result.filePath ? null : result.filePath;
      },
      alive: () => (agentScope() ? alive() : !quitting && documentScope.getStore()?.() === true),
      build: () =>
        createDiagnosticArchive({
          deployment,
          summary: diagnosticReport('recent'),
          startupDirectory: startupFailureDirectory(localAppData),
          runtime: createDiagnosticRuntime(diagnosticContext()),
          commandLogs: commandLog?.exportFiles() ?? {
            files: [],
            issues: [{ file: 'Logs', reason: 'unavailable' }],
          },
        }),
      write: (filename, bytes) => writeDiagnosticArchive(filename, bytes, expected),
    });
  }
  function requestPolicy() {
    return {
      quitting: quitting || cliExitPending,
      updateApplying:
        updateApplying ||
        dataLocation?.active === true ||
        (transitions.active !== null && agentScope()?.transition !== transitions.active),
      adoptionPending,
      administrationPending: controller?.state.administrationPending === true,
    };
  }
  function registerIpc(): void {
    if (!controller || !updates) throw new Error('IPC setup unavailable');
    registerHandlers({
      registry: commands,
      guard: {
        connect: (event) => {
          if (!guard) throw new Error(t.documentChanged);
          return guard.connect(event);
        },
        validate: (event, envelope) => {
          if (!guard) throw new Error(t.documentChanged);
          return guard.validate(event, envelope);
        },
        current: (lease) => guard?.current(lease) === true,
      },
      controller,
      updates,
      preflight,
      documentScope,
      theme,
      ipc: ipcMain,
      clipboard,
      commandLog: commandTrace,
      diagnosticReport,
      exportDiagnostics: diagnosticExport,
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
      policy: requestPolicy,
      requestAdministration,
      retryBackend: async () => {
        if (shutdown.inFlight) throw new Error('Wait for confirmed backend shutdown.');
        await startBackend();
        if (backend?.ready && !agentScope() && !agentHost) await runLogin();
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
