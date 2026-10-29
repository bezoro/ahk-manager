// Nonshipping presentation fixture. This file is never included by electron-builder.
import { contextBridge, ipcRenderer } from 'electron';
import type { AdministrationUiGuard, AhkManagerApi, HostState, ThemeState } from '../../src/shared/contracts.js';
import type { UpdateState } from '../../src/shared/updates.js';

const invoke = <T>(method: string, ...args: unknown[]): Promise<T> => ipcRenderer.invoke('fixture:invoke', { method, args }) as Promise<T>;
const listen = <T>(channel: string, callback: (value: T) => void): (() => void) => {
  const handler = (_event: Electron.IpcRendererEvent, value: T) => callback(value);
  ipcRenderer.on(channel, handler);
  return () => ipcRenderer.removeListener(channel, handler);
};
let guardProvider: (() => AdministrationUiGuard) | null = null;
const api: AhkManagerApi = {
  getUpdateState: () => invoke('getUpdateState'), checkUpdates: () => invoke('checkUpdates'),
  installUpdate: id => invoke('installUpdate', id), setUpdateToken: token => invoke('setUpdateToken', token),
  setUpdateChannel: channel => invoke('setUpdateChannel', channel),
  onUpdates: listener => listen('fixture:updates', listener),
  requestAdministration: () => invoke('requestAdministration'),
  onAdministrationPreflight: provider => { guardProvider = provider; return () => { if (guardProvider === provider) guardProvider = null; }; },
  getState: () => invoke('getState'), onState: listener => listen('fixture:state', listener), refresh: () => invoke('refresh'),
  addFiles: () => invoke('addFiles'), addFolder: () => invoke('addFolder'), removeScript: (...args) => invoke('removeScript', ...args),
  locateScript: (...args) => invoke('locateScript', ...args), deleteScriptFile: (...args) => invoke('deleteScriptFile', ...args),
  saveMetadata: (...args) => invoke('saveMetadata', ...args), saveCollection: (...args) => invoke('saveCollection', ...args), removeCollection: (...args) => invoke('removeCollection', ...args),
  operate: (...args) => invoke('operate', ...args), updateSelection: selection => { void invoke('updateSelection', selection); }, saveSettings: (...args) => invoke('saveSettings', ...args),
  pickInterpreter: () => invoke('pickInterpreter'), pickEditor: () => invoke('pickEditor'), getActivity: (...args) => invoke('getActivity', ...args),
  previewImport: () => invoke('previewImport'), commitImport: (...args) => invoke('commitImport', ...args), migrateProfile: () => invoke('migrateProfile'), transferStartup: () => invoke('transferStartup'),
  restoreBackup: () => invoke('restoreBackup'), recoverPending: () => invoke('recoverPending'), retryBackend: () => invoke('retryBackend'), copyText: (...args) => invoke('copyText', ...args),
  getTheme: () => invoke('getTheme'), onTheme: listener => listen('fixture:theme', listener), onOpenPreferences: listener => listen('fixture:preferences', listener),
};
contextBridge.exposeInMainWorld('ahkManager', api);
contextBridge.exposeInMainWorld('fixtureHarness', {
  readGuard: () => { try { return guardProvider?.() ?? null; } catch { return null; } },
  setResponse: (method: string, response: unknown) => invoke('setResponse', method, response),
  resolveResponse: (method: string, response: unknown) => invoke('resolveResponse', method, response),
  resetResponses: () => invoke('resetResponses'), openPreferences: () => invoke('openPreferences'),
  setUpdates: (value: UpdateState) => invoke('setUpdates', value), setState: (value: HostState) => invoke('setState', value),
  setTheme: (value: ThemeState) => invoke('setTheme', value), calls: () => invoke('calls'), clearCalls: () => invoke('clearCalls'),
});
window.addEventListener('DOMContentLoaded', () => {
  const banner = document.createElement('div');
  banner.id = 'fixture-warning'; banner.textContent = 'FIXTURE ISOLADA • dados de teste • nenhum script real';
  Object.assign(banner.style, { position: 'fixed', top: '9px', right: '160px', zIndex: '9999', font: '600 11px Segoe UI', color: '#865c00', background: '#fff4ce', padding: '3px 7px', borderRadius: '4px', pointerEvents: 'none' });
  document.body.append(banner);
});
