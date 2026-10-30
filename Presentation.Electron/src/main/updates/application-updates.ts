import { app, net, session } from 'electron';
import path from 'node:path';
import { UpdateService, type UpdateBoundary } from './update-service.js';
import { UpdateStore } from './update-store.js';
import { GitHubUpdateClient } from './github-updates.js';
import { createElectronUpdateFetch } from './electron-update-fetch.js';
import { configureUpdateNetwork } from './update-network-policy.js';
import { downloadRelease } from './electron-update-download.js';
import { updateRecoveryStatus } from './update-handoff.js';
import { type UpdateDeployment, validUpdateDeployment } from './update-deployment.js';
import { downloadPortableRelease } from './portable-update-download.js';
import {
  verifyCachedPortablePackageSignature,
  verifyPortableManifestPolicy,
} from './portable-update-signature.js';

type ApplicationUpdateBoundary = Pick<
  UpdateBoundary,
  'preflight' | 'confirm' | 'apply' | 'changed'
>;

export interface ApplicationUpdateOptions {
  deployment?: UpdateDeployment;
  root?: string;
  currentCommit?: string;
}

/** Builds an updater with its own network session, without granting renderer network access. */
export async function createApplicationUpdates(
  boundary: ApplicationUpdateBoundary,
  pollAllowed: () => boolean,
  options: ApplicationUpdateOptions = {},
): Promise<{ service: UpdateService; store: UpdateStore }> {
  const deployment = options.deployment ?? 'installed';
  if (!validUpdateDeployment(deployment) || (deployment === 'portable' && !options.root))
    throw new Error('The update storage location is invalid.');
  if (options.currentCommit !== undefined && !/^[a-f0-9]{40}$/.test(options.currentCommit))
    throw new Error('The running application build could not be identified.');
  // A dedicated Electron session retains OS proxy support and cannot grant renderer access.
  const network = session.fromPartition('electron-updater', { cache: false });
  configureUpdateNetwork(network);
  const root =
    options.root ??
    path.join(
      process.env.LOCALAPPDATA ?? path.join(app.getPath('home'), 'AppData', 'Local'),
      'Bezoro',
      'AhkManagerElectron',
      'Updates',
    );
  const store = new UpdateStore(root, deployment);
  const fetcher = createElectronUpdateFetch(network, (request) => net.request(request));
  const client = new GitHubUpdateClient(fetcher, deployment);
  const download: UpdateBoundary['download'] =
    deployment === 'portable'
      ? (release, progress) =>
          downloadPortableRelease(release, progress, {
            store,
            fetcher,
            verifyManifest: verifyPortableManifestPolicy,
          })
      : downloadRelease;
  const preserveCurrent: UpdateBoundary['preserveCurrent'] = async (progress) => {
    const current = (await store.cached()).find((item) => item.version === app.getVersion());
    if (current) {
      try {
        await store.verify(current);
        if (
          options.currentCommit &&
          (await store.portableProof(current)).commit !== options.currentCommit
        )
          throw new Error('The cached portable package does not match this running build.');
        await verifyCachedPortablePackageSignature(current.installerPath);
        return;
      } catch {
        /* Download an authenticated replacement before changing versions. */
      }
    }
    const release = await client.resolveVersion(app.getVersion());
    if (options.currentCommit && release.commit !== options.currentCommit)
      throw new Error('The published portable package does not match this running build.');
    const source = await download(release, progress);
    const saved = await store.cache(release.version, source, release.sha256, release.bytes);
    await store.verify(saved);
    await verifyCachedPortablePackageSignature(saved.installerPath);
  };
  const service = new UpdateService({
    ...boundary,
    version: app.getVersion(),
    store,
    deployment,
    ...(deployment === 'portable' ? { preserveCurrent } : {}),
    releases: (channel) => client.releases(channel),
    resolve: (id) => client.resolve(id),
    download,
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
