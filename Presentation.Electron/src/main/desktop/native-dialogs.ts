import { dialog, type BrowserWindow } from 'electron';
import path from 'node:path';
import fs from 'node:fs/promises';
import { hostText as t } from '../../shared/host-pt-BR.js';
import type { NativeBoundary } from '../controller.js';

/** Native dialogs retain the authenticated document scope across their asynchronous replies. */
export function createNativeDialogs(
  getWindow: () => BrowserWindow | null,
  alive: () => boolean,
): Pick<NativeBoundary, 'files' | 'confirm'> {
  async function confirm(title: string, detail: string): Promise<boolean> {
    const window = getWindow();
    if (!alive() || !window) return false;
    const response = await dialog.showMessageBox(window, {
      type: 'question',
      title,
      message: title,
      detail,
      buttons: [t.cancel, t.confirm],
      defaultId: 0,
      cancelId: 0,
      noLink: true,
    });
    return response.response === 1 && alive();
  }
  async function pick(
    kind: 'scripts' | 'locate' | 'folder' | 'interpreter' | 'editor' | 'import' | 'migration',
  ): Promise<string[]> {
    const window = getWindow();
    if (!alive() || !window) return [];
    const directory = kind === 'folder' || kind === 'migration';
    const response = await dialog.showOpenDialog(window, {
      title: t.pickerTitles[kind],
      properties: directory
        ? ['openDirectory', 'dontAddToRecent']
        : kind === 'scripts'
          ? ['openFile', 'multiSelections', 'dontAddToRecent']
          : ['openFile', 'dontAddToRecent'],
      filters: directory
        ? []
        : kind === 'scripts' || kind === 'locate'
          ? [{ name: t.fileFilters.scripts, extensions: ['ahk'] }]
          : kind === 'import'
            ? [{ name: t.fileFilters.catalog, extensions: ['json'] }]
            : [{ name: t.fileFilters.applications, extensions: ['exe'] }],
    });
    if (response.canceled || !alive()) return [];
    if (response.filePaths.length > 4096) throw new Error(t.fileLimit);
    for (const file of response.filePaths) {
      if (!path.isAbsolute(file) || file.includes('\0')) throw new Error(t.invalidPath);
      const info = await fs.stat(file);
      if (directory ? !info.isDirectory() : !info.isFile()) throw new Error(t.wrongFileType);
    }
    return response.filePaths;
  }
  return { files: pick, confirm };
}
