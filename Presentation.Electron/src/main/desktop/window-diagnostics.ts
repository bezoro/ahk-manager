import { app, type BrowserWindow } from 'electron';
import { APP_URL } from '../security.js';
import {
  createDiagnostics,
  type DiagnosticBoundary,
  type PageDiagnostics,
} from '../diagnostics.js';

/** Diagnostics observe only the owned window and never broaden renderer permissions. */
export function createWindowDiagnostics(
  window: BrowserWindow,
  summary: DiagnosticBoundary['summary'],
): PageDiagnostics | null {
  const diagnostics = createDiagnostics(process.argv, {
    capture: async () => {
      if (!window || window.isDestroyed() || window.webContents.getURL() !== APP_URL)
        throw new Error('Diagnostic document unavailable');
      const size = window.getContentBounds();
      if (size.width > 4096 || size.height > 4096)
        throw new Error('Diagnostic viewport exceeds bound');
      return (await window.webContents.capturePage()).toPNG();
    },
    summary,
  });
  if (diagnostics) {
    diagnostics.record('runtime', {
      electron: process.versions.electron,
      chromium: process.versions.chrome,
      node: process.versions.node,
      disableGpu: app.commandLine.hasSwitch('disable-gpu'),
      disableDirectComposition: app.commandLine.hasSwitch('disable-direct-composition'),
    });
    window.webContents.on('console-message', (details) =>
      diagnostics?.record('console', {
        level: details.level,
        message: details.message,
        line: details.lineNumber,
      }),
    );
    window.webContents.on('preload-error', (_event, _preload, error) =>
      diagnostics?.record('preload-error', { message: error.message }),
    );
    window.webContents.on('did-fail-load', (_event, code, description, _url, mainFrame) =>
      diagnostics?.record('did-fail-load', { code, description, mainFrame }),
    );
    window.webContents.on('render-process-gone', (_event, details) =>
      diagnostics?.record('render-process-gone', {
        reason: details.reason,
        exitCode: details.exitCode,
      }),
    );
    window.webContents.on('did-finish-load', () => diagnostics?.record('did-finish-load'));
    window.on('unresponsive', () => diagnostics?.record('unresponsive'));
    window.on('responsive', () => diagnostics?.record('responsive'));
    app.on('child-process-gone', (_event, details) =>
      diagnostics?.record('child-process-gone', {
        type: details.type,
        reason: details.reason,
        exitCode: details.exitCode,
      }),
    );
    app.once('will-quit', () => diagnostics?.close());
  }
  return diagnostics;
}
