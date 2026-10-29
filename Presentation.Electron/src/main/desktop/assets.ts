import { app, protocol, session } from 'electron';
import path from 'node:path';
import fs from 'node:fs/promises';
import { assetFromUrl, CSP } from '../security.js';
import { array, requireValid, text } from '../../shared/validation.js';

export async function serveAssets(): Promise<void> {
  const root = path.join(app.getAppPath(), 'dist', 'renderer');
  const manifest: unknown = JSON.parse(
    await fs.readFile(path.join(app.getAppPath(), 'dist', 'assets.json'), 'utf8'),
  );
  const allow = new Set(requireValid<string[]>(manifest, array(text(256), 256), 'asset manifest'));
  protocol.handle('ahk-manager', async (request) => {
    const name = request.method === 'GET' ? assetFromUrl(request.url, allow) : null;
    if (!name) return new Response('Forbidden', { status: 403 });
    const types: Record<string, string> = {
      '.html': 'text/html; charset=utf-8',
      '.js': 'text/javascript; charset=utf-8',
      '.css': 'text/css; charset=utf-8',
      '.svg': 'image/svg+xml',
      '.png': 'image/png',
      '.woff2': 'font/woff2',
    };
    try {
      return new Response(await fs.readFile(path.join(root, ...name.split('/'))), {
        headers: {
          'Content-Type': types[path.extname(name)] ?? 'application/octet-stream',
          'Content-Security-Policy': CSP,
          'X-Content-Type-Options': 'nosniff',
          'Cross-Origin-Resource-Policy': 'same-origin',
          'Cache-Control': 'no-store',
        },
      });
    } catch {
      return new Response('Not found', { status: 404 });
    }
  });
}

export function lockSession(): void {
  const target = session.defaultSession;
  target.setPermissionRequestHandler((_contents, _permission, callback) => callback(false));
  target.setPermissionCheckHandler(() => false);
  target.setDevicePermissionHandler(() => false);
  target.on('will-download', (event) => event.preventDefault());
  target.webRequest.onBeforeRequest((details, callback) =>
    callback({ cancel: !details.url.startsWith('ahk-manager://app/') }),
  );
}
