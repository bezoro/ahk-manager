import fs from 'node:fs/promises';
import path from 'node:path';
import { createHash } from 'node:crypto';
import type { RemoteUpdateRelease } from './github-updates.js';
import type { UpdateStore } from './update-store.js';
import { portableManifestFile, portableSignatureFile } from './update-deployment.js';
import { requirePortablePackageProof } from './portable-package-proof.js';
import { openUpdateAsset, readUpdateAsset } from './update-assets.js';

export type PortableManifestVerifier = (
  manifestPath: string,
  signaturePath: string,
) => Promise<void>;

export interface PortableDownloadOptions {
  fetcher: typeof fetch;
  store: UpdateStore;
  verifyManifest: PortableManifestVerifier;
  signal?: AbortSignal;
}

/** Stream a portable archive without loading the package into memory or executing it. */
export async function downloadPortableRelease(
  release: RemoteUpdateRelease,
  progress: (percent: number) => void,
  options: PortableDownloadOptions,
): Promise<string> {
  if (release.deployment !== 'portable' || !release.manifestText || release.metadataOnly)
    throw new Error('The portable release has not been verified. Check for updates again.');
  const proof = requirePortablePackageProof(release.manifestText, release);
  if (proof.sha512 !== release.sha512 || proof.commit !== release.commit)
    throw new Error('The portable release metadata changed. Check for updates again.');
  const root = path.join(options.store.root, 'downloads');
  await options.store.safeDirectory(root, true);
  const directory = await fs.mkdtemp(path.join(root, 'portable-'));
  const archive = path.join(directory, 'archive.zip');
  const manifest = path.join(directory, portableManifestFile);
  const signature = path.join(directory, portableSignatureFile);
  const signal = options.signal
    ? AbortSignal.any([options.signal, AbortSignal.timeout(30 * 60 * 1000)])
    : AbortSignal.timeout(30 * 60 * 1000);
  let completed = false;
  try {
    await options.store.writeNew(manifest, release.manifestText);
    if (release.signatureAssetId !== undefined) {
      const bytes = await readUpdateAsset(options.fetcher, release.signatureAssetId, 65536, signal);
      await options.store.writeNew(signature, bytes);
    }
    // Publisher verification happens before fetching the much larger archive.
    await options.verifyManifest(manifest, signature);
    const response = await openUpdateAsset(options.fetcher, release.assetId, signal);
    const declared = response.headers.get('content-length');
    if (declared !== null && (!/^\d+$/.test(declared) || Number(declared) !== release.bytes)) {
      await response.body!.cancel();
      throw new Error('The portable package length does not match its release metadata.');
    }
    const hash256 = createHash('sha256');
    const hash512 = createHash('sha512');
    let file: import('node:fs/promises').FileHandle;
    try {
      file = await fs.open(archive, 'wx', 0o600);
    } catch (error) {
      await response.body!.cancel().catch(() => {});
      throw error;
    }
    const reader = response.body!.getReader();
    let transferred = 0;
    try {
      for (;;) {
        const part = await reader.read();
        if (part.done) break;
        transferred += part.value.byteLength;
        if (transferred > release.bytes) {
          await reader.cancel();
          throw new Error('The portable package exceeded its declared size.');
        }
        hash256.update(part.value);
        hash512.update(part.value);
        for (let offset = 0; offset < part.value.byteLength;) {
          const written = await file.write(part.value, offset, part.value.byteLength - offset);
          if (written.bytesWritten < 1) throw new Error('The portable package could not be saved.');
          offset += written.bytesWritten;
        }
        progress(Math.min(100, (transferred * 100) / release.bytes));
      }
      if (
        transferred !== release.bytes ||
        hash256.digest('hex') !== release.sha256 ||
        hash512.digest('base64') !== release.sha512
      )
        throw new Error('The portable package failed its integrity check.');
      await file.sync();
    } finally {
      await reader.cancel().catch(() => {});
      reader.releaseLock();
      await file.close();
    }
    completed = true;
    return archive;
  } finally {
    if (!completed) {
      for (const file of [archive, manifest, signature]) await fs.unlink(file).catch(() => {});
      await fs.rmdir(directory).catch(() => {});
    }
  }
}
