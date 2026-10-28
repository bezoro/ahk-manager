import { NsisUpdater } from 'electron-updater';
import { Provider, type ProviderRuntimeOptions } from 'electron-updater/out/providers/Provider.js';
import type { UpdateInfo } from 'builder-util-runtime';
import { CancellationToken } from 'builder-util-runtime';
import { assetUrl, type RemoteUpdateRelease } from './github-updates.js';
import fs from 'node:fs/promises';
import path from 'node:path';
import { load, JSON_SCHEMA } from 'js-yaml';
import { verifySignature } from 'electron-updater/out/windowsExecutableCodeSignatureVerifier.js';

const silentLogger = { info() {}, warn() {}, error() {}, debug() {} };

/** A signed installation applies the same publisher policy to cached rollback packages. */
export async function verifyCachedInstallerSignature(file: string): Promise<void> {
  const configPath = path.join(process.resourcesPath, 'app-update.yml');
  const bytes = await fs.readFile(configPath);
  if (bytes.length > 65536) throw new Error('A política de assinatura da atualização é inválida.');
  const config = load(bytes.toString('utf8'), { schema: JSON_SCHEMA });
  if (!config || typeof config !== 'object') throw new Error('A política de assinatura da atualização é inválida.');
  const publisher = (config as Record<string, unknown>).publisherName;
  if (publisher === undefined || publisher === null) return;
  const names = typeof publisher === 'string' ? [publisher] : publisher;
  if (!Array.isArray(names) || !names.length || names.some(value => typeof value !== 'string' || !value.trim() || value.length > 1024)) throw new Error('A política de assinatura da atualização é inválida.');
  if (await verifySignature(names, file, silentLogger) !== null) throw new Error('A assinatura do instalador não corresponde ao editor desta instalação.');
}

/** electron-updater owns download caching, progress and SHA512/Authenticode verification. */
export async function downloadRelease(release: RemoteUpdateRelease, token: string | null, progress: (percent: number) => void): Promise<string> {
  class PinnedReleaseProvider extends Provider<UpdateInfo> {
    constructor(_configuration: unknown, _updater: unknown, runtime: ProviderRuntimeOptions) { super(runtime); }
    async getLatestVersion(): Promise<UpdateInfo> { return { version: release.version, releaseDate: release.publishedAt, files: [{ url: release.installerName, sha512: release.sha512, size: release.bytes }], path: release.installerName, sha512: release.sha512 }; }
    resolveFiles(info: UpdateInfo) { return [{ url: new URL(assetUrl(release.assetId)), info: info.files[0]! }]; }
    override get fileExtraDownloadHeaders() { return { Accept: 'application/octet-stream', ...(token ? { Authorization: `token ${token}` } : {}) }; }
  }
  const updater = new NsisUpdater({ provider: 'custom', updateProvider: PinnedReleaseProvider });
  updater.autoDownload = false; updater.autoInstallOnAppQuit = false; updater.allowDowngrade = true;
  updater.disableDifferentialDownload = true; updater.disableWebInstaller = true;
  // Never persist a token, signed asset URL or HTTP error body through library logging.
  updater.logger = silentLogger;
  const cancellation = new CancellationToken();
  updater.on('download-progress', value => { if (value.transferred > release.bytes) cancellation.cancel(); else progress(value.percent); });
  try {
    const checked = await updater.checkForUpdates();
    if (!checked?.isUpdateAvailable) throw new Error('Versão indisponível.');
    const files = await updater.downloadUpdate(cancellation);
    if (files.length !== 1 || !files[0]) throw new Error('Instalador indisponível.');
    return files[0];
  } catch { throw new Error('Não foi possível baixar e verificar o instalador. Confira a conexão e o acesso ao GitHub e tente novamente.'); }
  finally { updater.removeAllListeners(); }
}
