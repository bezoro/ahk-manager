import { BrowserWindow } from 'electron';
import path from 'node:path';
import { hostText as t } from '../../shared/host-pt-BR.js';

export function createMainWindow(): BrowserWindow {
  return new BrowserWindow({
    width: 1200,
    height: 780,
    minWidth: 760,
    minHeight: 560,
    show: false,
    title: t.title,
    backgroundColor: '#00000000',
    titleBarStyle: 'hidden',
    titleBarOverlay: { height: 44 },
    autoHideMenuBar: true,
    webPreferences: {
      preload: path.join(__dirname, '../preload/index.cjs'),
      nodeIntegration: false,
      contextIsolation: true,
      sandbox: true,
      webSecurity: true,
      allowRunningInsecureContent: false,
      devTools: false,
      spellcheck: false,
    },
  });
}
