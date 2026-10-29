import { app, safeStorage, session } from 'electron';
import path from 'node:path';
import { UpdateService, type UpdateBoundary } from './update-service.js';
import { UpdateStore } from './update-store.js';
import { GitHubUpdateClient } from './github-updates.js';
import { downloadRelease } from './electron-update-download.js';
import { updateRecoveryStatus } from './update-handoff.js';

type ApplicationUpdateBoundary = Pick<
  UpdateBoundary,
  'preflight' | 'confirm' | 'apply' | 'changed'
>;

/** Builds an updater with its own network session, without granting renderer network access. */
export async function createApplicationUpdates(
  boundary: ApplicationUpdateBoundary,
  pollAllowed: () => boolean,
): Promise<{ service: UpdateService; store: UpdateStore }> {
  // A dedicated Electron session retains OS proxy support and cannot grant renderer access.
  const network = session.fromPartition('electron-updater', { cache: false });
  network.setPermissionRequestHandler((_contents, _permission, callback) => callback(false));
  network.setPermissionCheckHandler(() => false);
  network.webRequest.onBeforeRequest((details, callback) => {
    let allowed = false;
    try {
      const url = new URL(details.url);
      allowed =
        url.protocol === 'https:' &&
        !url.username &&
        !url.password &&
        !url.port &&
        ((url.hostname === 'api.github.com' &&
          url.pathname.startsWith('/repos/bezoro/ahk-manager/releases')) ||
          ['release-assets.githubusercontent.com', 'objects.githubusercontent.com'].includes(
            url.hostname,
          ));
    } catch {
      /* Only the fixed update origin is permitted. */
    }
    callback({ cancel: !allowed });
  });
  network.webRequest.onBeforeSendHeaders((details, callback) => {
    const headers = { ...details.requestHeaders };
    if (new URL(details.url).hostname !== 'api.github.com')
      for (const name of Object.keys(headers))
        if (['authorization', 'cookie', 'proxy-authorization'].includes(name.toLowerCase()))
          delete headers[name];
    callback({ requestHeaders: headers });
  });
  const root = path.join(
    process.env.LOCALAPPDATA ?? path.join(app.getPath('home'), 'AppData', 'Local'),
    'Bezoro',
    'AhkManagerElectron',
    'Updates',
  );
  const store = new UpdateStore(root, {
    available: () => safeStorage.isEncryptionAvailable(),
    encrypt: (value) => safeStorage.encryptString(value),
    decrypt: (value) => safeStorage.decryptString(value),
  });
  const client = new GitHubUpdateClient(network.fetch.bind(network) as typeof fetch);
  const service = new UpdateService({
    ...boundary,
    version: app.getVersion(),
    store,
    releases: (token) => client.releases(token),
    resolve: (id, token) => client.resolve(id, token),
    download: downloadRelease,
    recovery: () => updateRecoveryStatus(store),
  });
  await service.initialize();
  void service.check();
  const interval = setInterval(
    () => {
      if (pollAllowed()) void service.check();
    },
    15 * 60 * 1000,
  );
  interval.unref();
  return { service, store };
}
