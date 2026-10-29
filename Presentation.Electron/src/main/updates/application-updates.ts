import { app, net, session } from 'electron';
import path from 'node:path';
import { UpdateService, type UpdateBoundary } from './update-service.js';
import { UpdateStore } from './update-store.js';
import { GitHubUpdateClient } from './github-updates.js';
import { createElectronUpdateFetch } from './electron-update-fetch.js';
import { configureUpdateNetwork } from './update-network-policy.js';
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
  configureUpdateNetwork(network);
  const root = path.join(
    process.env.LOCALAPPDATA ?? path.join(app.getPath('home'), 'AppData', 'Local'),
    'Bezoro',
    'AhkManagerElectron',
    'Updates',
  );
  const store = new UpdateStore(root);
  const client = new GitHubUpdateClient(
    createElectronUpdateFetch(network, (options) => net.request(options)),
  );
  const service = new UpdateService({
    ...boundary,
    version: app.getVersion(),
    store,
    releases: (channel) => client.releases(channel),
    resolve: (id) => client.resolve(id),
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
