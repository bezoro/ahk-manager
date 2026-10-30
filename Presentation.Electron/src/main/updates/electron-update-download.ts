import { NsisUpdater } from 'electron-updater';
import { Provider, type ProviderRuntimeOptions } from 'electron-updater/out/providers/Provider.js';
import type { UpdateInfo } from 'builder-util-runtime';
import { CancellationToken } from 'builder-util-runtime';
import { assetUrl, type RemoteUpdateRelease } from './github-updates.js';
import { updatePublisherNames } from './update-publisher-policy.js';
import { verifySignature } from 'electron-updater/out/windowsExecutableCodeSignatureVerifier.js';
import { isTestUpdateVersion } from '../../shared/update-versions.js';

const silentLogger = { info() {}, warn() {}, error() {}, debug() {} };

/** A signed installation applies the same publisher policy to cached rollback packages. */
export async function verifyCachedInstallerSignature(file: string): Promise<void> {
  const names = await updatePublisherNames();
  if (!names.length) return;
  if ((await verifySignature(names, file, silentLogger)) !== null)
    throw new Error("The installer signature does not match this installation's publisher.");
}

/** electron-updater owns download caching, progress and SHA512/Authenticode verification. */
export async function downloadRelease(
  release: RemoteUpdateRelease,
  progress: (percent: number) => void,
): Promise<string> {
  class PinnedReleaseProvider extends Provider<UpdateInfo> {
    constructor(_configuration: unknown, _updater: unknown, runtime: ProviderRuntimeOptions) {
      super(runtime);
    }
    async getLatestVersion(): Promise<UpdateInfo> {
      return {
        version: release.version,
        releaseDate: release.publishedAt,
        files: [{ url: release.installerName, sha512: release.sha512, size: release.bytes }],
        path: release.installerName,
        sha512: release.sha512,
      };
    }
    resolveFiles(info: UpdateInfo) {
      return [{ url: new URL(assetUrl(release.assetId)), info: info.files[0]! }];
    }
    override get fileExtraDownloadHeaders() {
      return {
        Accept: 'application/octet-stream',
      };
    }
  }
  const updater = new NsisUpdater({ provider: 'custom', updateProvider: PinnedReleaseProvider });
  updater.autoDownload = false;
  updater.autoInstallOnAppQuit = false;
  updater.allowDowngrade = true;
  updater.allowPrerelease = isTestUpdateVersion(release.version);
  updater.disableDifferentialDownload = true;
  updater.disableWebInstaller = true;
  // Never persist a signed asset URL or HTTP error body through library logging.
  updater.logger = silentLogger;
  const cancellation = new CancellationToken();
  updater.on('download-progress', (value) => {
    if (value.transferred > release.bytes) cancellation.cancel();
    else progress(value.percent);
  });
  try {
    const checked = await updater.checkForUpdates();
    if (!checked?.isUpdateAvailable) throw new Error('This version is unavailable.');
    const files = await updater.downloadUpdate(cancellation);
    if (files.length !== 1 || !files[0]) throw new Error('The installer is unavailable.');
    return files[0];
  } catch {
    throw new Error(
      'The installer could not be downloaded and verified. Check your connection and try again.',
    );
  } finally {
    updater.removeAllListeners();
  }
}
