import fs from 'node:fs/promises';
import path from 'node:path';
import { createHash, randomUUID } from 'node:crypto';
import { createReadStream } from 'node:fs';
import {
  type UpdateDeployment,
  packageFileName,
  packageProtocol,
  portableManifestFile,
  portableSignatureFile,
  validUpdateDeployment,
} from './update-deployment.js';
import {
  requirePortablePackageProof,
  type PortablePackageProof,
} from './portable-package-proof.js';
import {
  validUpdateChannel,
  validUpdateVersion,
  type UpdateChannel,
} from '../../shared/update-versions.js';

export interface CachedPackage {
  version: string;
  archivePath: string;
  sha256: string;
  bytes: number;
}
const version = validUpdateVersion;
const digest = (value: unknown): value is string =>
  typeof value === 'string' && /^[a-f0-9]{64}$/i.test(value);
const missing = (error: unknown): boolean => (error as NodeJS.ErrnoException)?.code === 'ENOENT';

/** Portable archives live outside App and Profile. The historic installers directory preserves existing histories. */
export class UpdateStore {
  constructor(
    readonly root: string,
    readonly deployment: UpdateDeployment = 'portable',
  ) {
    if (!validUpdateDeployment(deployment)) throw new Error('The update deployment is invalid.');
    if (!path.isAbsolute(root)) throw new Error('The update cache is invalid.');
  }
  async safeDirectory(directory: string, create = false): Promise<void> {
    const absolute = path.resolve(directory);
    const parent = path.dirname(absolute);
    if (parent !== absolute) await this.safeDirectory(parent, create);
    try {
      const stat = await fs.lstat(absolute);
      if (!stat.isDirectory() || stat.isSymbolicLink())
        throw new Error('The update directory has been redirected.');
    } catch (error) {
      if (!create || !missing(error)) throw error;
      try {
        await fs.mkdir(absolute, { mode: 0o700 });
      } catch (nested) {
        if ((nested as NodeJS.ErrnoException).code !== 'EEXIST') throw nested;
      }
      const stat = await fs.lstat(absolute);
      if (!stat.isDirectory() || stat.isSymbolicLink())
        throw new Error('The update directory is invalid.');
    }
  }
  async regular(file: string, maximum = 1024 * 1024 * 1024): Promise<import('node:fs').Stats> {
    await this.safeDirectory(path.dirname(file));
    const stat = await fs.lstat(file);
    if (
      !stat.isFile() ||
      stat.isSymbolicLink() ||
      stat.nlink !== 1 ||
      stat.size < 1 ||
      stat.size > maximum
    )
      throw new Error('The update file is invalid.');
    return stat;
  }
  async hash(file: string): Promise<string> {
    const before = await this.regular(file);
    const hash = createHash('sha256');
    for await (const chunk of createReadStream(file)) hash.update(chunk);
    const after = await this.regular(file);
    if (
      before.ino !== after.ino ||
      before.dev !== after.dev ||
      before.size !== after.size ||
      before.mtimeMs !== after.mtimeMs ||
      before.ctimeMs !== after.ctimeMs
    )
      throw new Error('The update file changed during verification.');
    return hash.digest('hex');
  }
  async writeNew(file: string, value: string | Buffer): Promise<void> {
    await this.safeDirectory(path.dirname(file), true);
    await fs.writeFile(file, value, { flag: 'wx', mode: 0o600 });
  }
  /** Retire only an owned regular credential file; never follow redirected entries. */
  async removeLegacyAccess(): Promise<void> {
    const file = path.join(this.root, 'github-token.bin');
    try {
      await this.safeDirectory(this.root);
      const stat = await fs.lstat(file);
      if (!stat.isFile() || stat.isSymbolicLink() || stat.nlink !== 1) return;
      await fs.unlink(file);
    } catch (error) {
      if (!missing(error)) throw error;
    }
  }
  async readChannel(): Promise<UpdateChannel> {
    const file = path.join(this.root, 'update-channel.json');
    try {
      await this.regular(file, 1024);
      const document: unknown = JSON.parse(await fs.readFile(file, 'utf8'));
      if (!document || typeof document !== 'object' || Array.isArray(document))
        throw new Error('Invalid channel preference.');
      const value = document as Record<string, unknown>;
      if (
        Object.keys(value).length !== 2 ||
        value.schemaVersion !== 1 ||
        !validUpdateChannel(value.channel)
      )
        throw new Error('Invalid channel preference.');
      return value.channel;
    } catch (error) {
      if (missing(error)) return 'stable';
      throw new Error('The update channel could not be read. Select the channel again.');
    }
  }
  async saveChannel(channel: UpdateChannel): Promise<void> {
    if (!validUpdateChannel(channel)) throw new Error('The update channel is invalid.');
    await this.safeDirectory(this.root, true);
    const file = path.join(this.root, 'update-channel.json');
    try {
      // Size bounds protect reads. An empty or oversized corrupt preference can
      // still be repaired by atomically replacing our own regular, unlinked file.
      const existing = await fs.lstat(file);
      if (!existing.isFile() || existing.isSymbolicLink() || existing.nlink !== 1)
        throw new Error('The update file is invalid.');
    } catch (error) {
      if (!missing(error)) throw error;
    }
    const temporary = path.join(this.root, `channel-${randomUUID()}.tmp`);
    await this.writeNew(temporary, JSON.stringify({ schemaVersion: 1, channel }));
    try {
      await fs.rename(temporary, file);
    } catch (error) {
      await fs.unlink(temporary).catch(() => {});
      throw error;
    }
  }
  async cached(): Promise<CachedPackage[]> {
    const root = path.join(this.root, 'installers');
    try {
      await this.safeDirectory(root);
    } catch (error) {
      if (missing(error)) return [];
      throw error;
    }
    const result: CachedPackage[] = [];
    for (const name of (await fs.readdir(root)).filter(version).slice(0, 1000)) {
      try {
        const receipt = path.join(root, name, 'receipt.json');
        await this.regular(receipt, 16384);
        const data: unknown = JSON.parse(
          (await fs.readFile(receipt)).toString('utf8').replace(/^\uFEFF/, ''),
        );
        if (!data || typeof data !== 'object') continue;
        const record = data as Record<string, unknown>;
        if (
          record.schemaVersion !== 1 ||
          record.updateProtocol !== packageProtocol() ||
          record.version !== name ||
          !digest(record.sha256)
        )
          continue;
        if (
          Object.keys(record).length !== 5 ||
          !Number.isSafeInteger(record.bytes) ||
          typeof record.sha256 !== 'string' ||
          !/^[a-f0-9]{64}$/.test(record.sha256)
        )
          continue;
        const archivePath = path.join(root, name, packageFileName());
        const stat = await this.regular(archivePath);
        if (record.bytes !== undefined && record.bytes !== stat.size) continue;

        const proof = path.join(root, name, portableManifestFile);
        await this.regular(proof, 65536);
        requirePortablePackageProof(
          new TextDecoder('utf-8', { fatal: true }).decode(await fs.readFile(proof)),
          { version: name, sha256: record.sha256.toLowerCase(), bytes: stat.size },
        );

        result.push({
          version: name,
          archivePath,
          sha256: record.sha256.toLowerCase(),
          bytes: stat.size,
        });
      } catch {
        /* An incomplete or redirected cache never becomes an executable choice. */
      }
    }
    return result;
  }
  async verify(entry: CachedPackage): Promise<void> {
    if (
      !version(entry.version) ||
      entry.archivePath !== path.join(this.root, 'installers', entry.version, packageFileName()) ||
      !digest(entry.sha256)
    )
      throw new Error('The package is outside the version history.');
    const stat = await this.regular(entry.archivePath);
    if (stat.size !== entry.bytes || (await this.hash(entry.archivePath)) !== entry.sha256)
      throw new Error(
        'The package integrity check failed. Check for updates to download another copy.',
      );

    await this.portableProof(entry);
  }
  async portableProof(entry: CachedPackage): Promise<PortablePackageProof> {
    if (
      !version(entry.version) ||
      entry.archivePath !== path.join(this.root, 'installers', entry.version, 'archive.zip')
    )
      throw new Error('The portable package is outside the version history.');
    const proof = path.join(path.dirname(entry.archivePath), portableManifestFile);
    await this.regular(proof, 65536);
    return requirePortablePackageProof(
      new TextDecoder('utf-8', { fatal: true }).decode(await fs.readFile(proof)),
      entry,
    );
  }
  private async removePortableDownload(source: string): Promise<void> {
    const directory = path.dirname(source);
    if (
      path.dirname(directory) !== path.join(this.root, 'downloads') ||
      !/^portable-[A-Za-z0-9]+$/.test(path.basename(directory)) ||
      path.basename(source) !== 'archive.zip'
    )
      return;
    try {
      await this.safeDirectory(directory);
      const allowed = new Set(['archive.zip', portableManifestFile, portableSignatureFile]);
      const files = await fs.readdir(directory);
      if (files.some((name) => !allowed.has(name))) return;
      for (const name of files) await this.regular(path.join(directory, name));
      for (const name of files) await fs.unlink(path.join(directory, name));
      await fs.rmdir(directory);
    } catch {
      /* A completed package remains valid if temporary cleanup fails. */
    }
  }
  private async cachePortableProof(
    source: string,
    destination: string,
    expected: { version: string; sha256: string; bytes: number },
  ): Promise<void> {
    const manifest = path.join(source, portableManifestFile);
    try {
      await this.regular(manifest, 65536);
    } catch (error) {
      if (missing(error)) throw new Error('The portable package release metadata is unavailable.');
      throw error;
    }
    const bytes = await fs.readFile(manifest);
    requirePortablePackageProof(new TextDecoder('utf-8', { fatal: true }).decode(bytes), expected);
    for (const name of [portableManifestFile, portableSignatureFile]) {
      const input = path.join(source, name);
      try {
        await this.regular(input, 65536);
      } catch (error) {
        if (missing(error) && name === portableSignatureFile) continue;
        throw error;
      }
      const target = path.join(destination, name);
      try {
        await this.regular(target, 65536);
      } catch (error) {
        if (!missing(error)) throw error;
      }
      const temporary = path.join(destination, `proof-${randomUUID()}.tmp`);
      await fs.copyFile(input, temporary, 1);
      try {
        if (name === portableManifestFile)
          requirePortablePackageProof(
            new TextDecoder('utf-8', { fatal: true }).decode(await fs.readFile(temporary)),
            expected,
          );
        await fs.rename(temporary, target);
      } finally {
        await fs.unlink(temporary).catch(() => {});
      }
    }
  }
  async cache(
    versionName: string,
    source: string,
    sha256: string,
    bytes: number,
  ): Promise<CachedPackage> {
    if (!version(versionName) || !digest(sha256)) throw new Error('The update version is invalid.');
    if ((await this.regular(source)).size !== bytes || (await this.hash(source)) !== sha256)
      throw new Error('The downloaded package failed its integrity check.');

    const proof = path.join(path.dirname(source), portableManifestFile);
    await this.regular(proof, 65536);
    requirePortablePackageProof(
      new TextDecoder('utf-8', { fatal: true }).decode(await fs.readFile(proof)),
      { version: versionName, sha256, bytes },
    );

    const directory = path.join(this.root, 'installers', versionName);
    await this.safeDirectory(directory, true);
    const archivePath = path.join(directory, packageFileName());
    let existing = (await this.cached()).find(
      (item) => item.version === versionName && item.sha256 === sha256 && item.bytes === bytes,
    );
    if (existing) {
      try {
        await this.verify(existing);
      } catch {
        /* Preserve corrupt bytes below and replace through a fresh file. */
        existing = undefined;
      }
    }
    if (existing) {
      // A valid archive can still need a fresh signature after interrupted cache
      // publication. Retain the archive while repairing its downloaded proof.

      await this.cachePortableProof(path.dirname(source), directory, {
        version: versionName,
        sha256,
        bytes,
      });
      await this.removePortableDownload(source);

      return existing;
    }
    const temporary = path.join(directory, `download-${randomUUID()}.tmp`);
    await fs.copyFile(source, temporary, 1);
    if ((await this.hash(temporary)) !== sha256)
      throw new Error('The package changed while preparing the update.');
    try {
      await this.regular(archivePath);
      await fs.rename(archivePath, path.join(directory, `preserved-${randomUUID()}.zip`));
    } catch (error) {
      if (!missing(error)) throw error;
    }
    await fs.rename(temporary, archivePath);
    await this.cachePortableProof(path.dirname(source), directory, {
      version: versionName,
      sha256,
      bytes,
    });
    const receipt = path.join(directory, 'receipt.json');
    const tempReceipt = path.join(directory, `receipt-${randomUUID()}.tmp`);
    await this.writeNew(
      tempReceipt,
      JSON.stringify({
        schemaVersion: 1,
        updateProtocol: packageProtocol(),
        version: versionName,
        sha256,
        bytes,
      }),
    );
    await fs.rename(tempReceipt, receipt);
    await this.removePortableDownload(source);
    return { version: versionName, archivePath, sha256, bytes };
  }
}
