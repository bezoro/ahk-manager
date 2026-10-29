import { nativeImage, Tray, type MenuItemConstructorOptions } from 'electron';
import path from 'node:path';
import { hostText as t } from '../../shared/host-pt-BR.js';
import type { StartupContinuationOffer } from '../../shared/contracts.js';

export interface TrayState {
  adoptionPending: boolean;
  backendReady: boolean;
  quitting: boolean;
  continuationOffer: StartupContinuationOffer | null;
  administrationActive: boolean;
  windowVisible: boolean;
  runningNames: string[];
  stopExitActive: boolean;
  connectionReady: boolean;
  progress: { done: number; total: number };
}

export interface TrayActions {
  checkAdoption(): void;
  continueStartup(): void;
  activate(): void;
  toggleWindow(): void;
  preferences(): void;
  quit(): void;
  stopScriptsAndQuit(): void;
  cancelStopExit(): void;
}

export function createTrayIcon(activate: () => void): Tray | null {
  try {
    const image = nativeImage.createFromPath(path.join(process.resourcesPath, 'tray.png'));
    if (image.isEmpty()) throw new Error('Tray icon unavailable');
    const tray = new Tray(image);
    tray.setToolTip(t.title);
    tray.on('double-click', activate);
    return tray;
  } catch {
    return null;
  }
}

/** Rendering the menu never performs an operation; handlers keep authority in the coordinator. */
export function trayMenu(state: TrayState, actions: TrayActions): MenuItemConstructorOptions[] {
  return [
    {
      label: t.continuationCheck,
      visible: state.adoptionPending,
      enabled: state.adoptionPending && state.backendReady && !state.quitting,
      click: actions.checkAdoption,
    },
    {
      label: t.continuationMenu,
      visible: state.continuationOffer !== null,
      enabled:
        state.continuationOffer?.canResume === true &&
        !state.adoptionPending &&
        !state.administrationActive &&
        !state.quitting,
      click: actions.continueStartup,
    },
    { label: t.tray.open, click: actions.activate },
    { label: state.windowVisible ? t.tray.hide : t.tray.show, click: actions.toggleWindow },
    { type: 'separator' },
    {
      label: t.tray.running(state.runningNames.length),
      submenu: state.runningNames.length
        ? state.runningNames.map((label) => ({ label, enabled: false }))
        : [{ label: t.tray.noneRunning, enabled: false }],
    },
    {
      label: t.tray.preferences,
      enabled: !state.stopExitActive && !state.administrationActive,
      click: actions.preferences,
    },
    { type: 'separator' },
    { label: t.tray.exit, enabled: !state.stopExitActive && !state.quitting, click: actions.quit },
    {
      label: t.tray.stopExit,
      enabled: !state.stopExitActive && !state.quitting && state.connectionReady,
      click: actions.stopScriptsAndQuit,
    },
    {
      label: t.tray.stopping(state.progress.done, state.progress.total),
      visible: state.stopExitActive,
      enabled: false,
    },
    { label: t.tray.cancelStopExit, visible: state.stopExitActive, click: actions.cancelStopExit },
  ];
}
