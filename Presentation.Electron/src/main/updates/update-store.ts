import fs from 'node:fs/promises';
import path from 'node:path';
import { UpdateStaging } from './update-staging.js';
import { createHash, randomUUID } from 'node:crypto';
import { constants, type BigIntStats, type Stats } from 'node:fs';
import { setTimeout as delay } from 'node:timers/promises';
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
const maximumUpdateBytes = 1024 * 1024 * 1024;
const verificationRetryDelayMs = 25;

function requireRegular(stat: Stats | BigIntStats, maximum: number): void {
  if (
    !stat.isFile() ||
    stat.isSymbolicLink() ||
    (stat.nlink !== 1 && stat.nlink !== 1n) ||
    stat.size < 1 ||
    stat.size > maximum
  )
    throw new Error('The update file is invalid.');
}

const changed = () => new Error('The update file changed during verification.');

function sameFile(first: BigIntStats, second: BigIntStats): boolean {
  return (
    first.dev === second.dev &&
    first.ino === second.ino &&
    first.birthtimeNs === second.birthtimeNs &&
    first.size === second.size &&
    first.nlink === second.nlink &&
    first.mode === second.mode
  );
}

function sameTimestamps(first: BigIntStats, second: BigIntStats): boolean {
  return first.mtimeNs === second.mtimeNs && first.ctimeNs === second.ctimeNs;
}

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
  async regular(file: string, maximum = maximumUpdateBytes): Promise<Stats> {
    await this.safeDirectory(path.dirname(file));
    const stat = await fs.lstat(file);
    requireRegular(stat, maximum);
    return stat;
  }

  private async hashStamp(file: string): Promise<BigIntStats> {
    await this.safeDirectory(path.dirname(file));
    const stat = await fs.lstat(file, { bigint: true });
    requireRegular(stat, maximumUpdateBytes);
    return stat;
  }

  private async readHash(
    file: string,
    expected?: BigIntStats,
  ): Promise<{ stamp: BigIntStats; digest: string; stable: boolean }> {
    const before = await this.hashStamp(file);
    if (expected && !sameFile(expected, before)) throw changed();
    const handle = await fs.open(file, constants.O_RDONLY | (constants.O_NOFOLLOW ?? 0));
    try {
      const opened = await handle.stat({ bigint: true });
      requireRegular(opened, maximumUpdateBytes);
      if (!sameFile(before, opened)) throw changed();

      const hash = createHash('sha256');
      const size = Number(before.size);
      const buffer = Buffer.allocUnsafe(Math.min(64 * 1024, size));
      let position = 0;
      while (position < size) {
        const count = Math.min(buffer.length, size - position);
        const { bytesRead } = await handle.read(buffer, 0, count, position);
        if (bytesRead === 0) throw changed();
        hash.update(buffer.subarray(0, bytesRead));
        position += bytesRead;
      }
      if ((await handle.read(buffer, 0, 1, position)).bytesRead !== 0) throw changed();

      const read = await handle.stat({ bigint: true });
      const atPath = await this.hashStamp(file);
      requireRegular(read, maximumUpdateBytes);
      if (!sameFile(before, read) || !sameFile(before, atPath)) throw changed();
      return {
        stamp: before,
        digest: hash.digest('hex'),
        stable:
          sameTimestamps(before, opened) &&
          sameTimestamps(before, read) &&
          sameTimestamps(before, atPath),
      };
    } finally {
      await handle.close();
    }
  }

  async hash(file: string): Promise<string> {
    const first = await this.readHash(file);
    if (first.stable) return first.digest;
    // Only a complete read with timestamp drift can reach the retry.
    await delay(verificationRetryDelayMs);
    const second = await this.readHash(file, first.stamp);
    if (!second.stable || second.digest !== first.digest) throw changed();
    return second.digest;
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
  async verify(entry: CachedPackage, expectedManifest?: string): Promise<void> {
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

    await this.portableProof(entry, expectedManifest);
  }
  async portableProof(
    entry: CachedPackage,
    expectedManifest?: string,
  ): Promise<PortablePackageProof> {
    if (
      !version(entry.version) ||
      entry.archivePath !== path.join(this.root, 'installers', entry.version, 'archive.zip')
    )
      throw new Error('The portable package is outside the version history.');
    const proof = path.join(path.dirname(entry.archivePath), portableManifestFile);
    await this.regular(proof, 65536);
    const manifest = new TextDecoder('utf-8', { fatal: true }).decode(await fs.readFile(proof));
    if (expectedManifest !== undefined && manifest !== expectedManifest)
      throw new Error('The cached update metadata does not match the checked release.');
    return requirePortablePackageProof(manifest, entry);
  }
  private readonly downloads = new Map<string, UpdateStaging>();

  async beginPortableDownload(): Promise<UpdateStaging> {
    const root = path.join(this.root, 'downloads');
    await this.safeDirectory(root, true);
    const stage = await UpdateStaging.create(path.join(root, 'portable-'), (dir) =>
      this.safeDirectory(dir),
    );
    this.downloads.set(path.join(stage.directory, 'archive.zip'), stage);
    return stage;
  }

  async discardPortableDownload(source: string): Promise<void> {
    const stage = this.downloads.get(source);
    try {
      if (!stage) throw new Error('Unknown staging directory.');
      await stage.discard();
    } catch {
      throw new Error('The temporary update download could not be removed.');
    } finally {
      this.downloads.delete(source);
    }
  }

  private async signature(directory: string): Promise<Buffer | undefined> {
    const file = path.join(directory, portableSignatureFile);
    try {
      await this.regular(file, 65536);
      return await fs.readFile(file);
    } catch (error) {
      if (missing(error)) return undefined;
      throw error;
    }
  }

  async cache(
    versionName: string,
    source: string,
    sha256: string,
    bytes: number,
  ): Promise<CachedPackage> {
    try {
      return await this.publishCache(versionName, source, sha256, bytes);
    } finally {
      // An inaccessible or replaced download stays available for inspection. Cleanup
      // cannot change the publication result or hide its original failure.
      if (this.downloads.has(source)) await this.discardPortableDownload(source).catch(() => {});
    }
  }

  private async publishCache(
    versionName: string,
    source: string,
    sha256: string,
    bytes: number,
  ): Promise<CachedPackage> {
    if (!version(versionName) || !digest(sha256)) throw new Error('The update version is invalid.');
    if ((await this.regular(source)).size !== bytes || (await this.hash(source)) !== sha256)
      throw new Error('The downloaded package failed its integrity check.');
    const proofPath = path.join(path.dirname(source), portableManifestFile);
    await this.regular(proofPath, 65536);
    const proof = await fs.readFile(proofPath);
    const manifest = new TextDecoder('utf-8', { fatal: true }).decode(proof);
    requirePortablePackageProof(manifest, { version: versionName, sha256, bytes });
    const signature = await this.signature(path.dirname(source));
    const history = path.join(this.root, 'installers');
    await this.safeDirectory(history, true);
    const directory = path.join(history, versionName);
    let original: BigIntStats | undefined;
    try {
      await this.safeDirectory(directory);
      original = await fs.lstat(directory, { bigint: true });
    } catch (error) {
      if (!missing(error)) throw error;
    }
    const entry = {
      version: versionName,
      archivePath: path.join(directory, packageFileName()),
      sha256,
      bytes,
    };
    if (original) {
      const existing = (await this.cached()).find(
        (item) => item.version === versionName && item.sha256 === sha256 && item.bytes === bytes,
      );
      if (existing) {
        try {
          await this.verify(existing, manifest);
          const saved = await this.signature(directory);
          if (signature ? saved && signature.equals(saved) : !saved) return existing;
        } catch {
          // Replace the complete edition, retaining the earlier archive and its proof together.
        }
      }
    }
    const stage = await UpdateStaging.create(path.join(history, '.cache-'), (dir) =>
      this.safeDirectory(dir),
    );
    let published = false;
    try {
      const archive = await stage.open(packageFileName());
      try {
        const input = await fs.open(source, constants.O_RDONLY | (constants.O_NOFOLLOW ?? 0));
        try {
          const opened = await input.stat();
          requireRegular(opened, maximumUpdateBytes);
          if (opened.size !== bytes) throw changed();
          for await (const chunk of input.createReadStream({ end: bytes - 1 })) {
            await archive.writeFile(chunk as Buffer);
          }
          await archive.sync();
        } finally {
          await input.close();
        }
      } finally {
        await archive.close();
      }
      if ((await this.hash(path.join(stage.directory, packageFileName()))) !== sha256)
        throw new Error('The package changed while preparing the update.');
      await stage.write(portableManifestFile, proof);
      if (signature) await stage.write(portableSignatureFile, signature);
      await stage.write(
        'receipt.json',
        JSON.stringify({
          schemaVersion: 1,
          updateProtocol: packageProtocol(),
          version: versionName,
          sha256,
          bytes,
        }),
      );
      await stage.verify();
      const preserved = path.join(history, 'preserved-' + randomUUID());
      if (original) {
        await this.safeDirectory(directory);
        const current = await fs.lstat(directory, { bigint: true });
        if (!sameFile(current, original)) throw changed();
        await fs.rename(directory, preserved);
      }
      try {
        await fs.rename(stage.directory, directory);
      } catch (error) {
        if (original) {
          // Restore only to an absent name. Preserve the full backup on any conflict.
          const occupant = await fs.lstat(directory).catch((e: unknown) => {
            if (!missing(e)) throw e;
            return null;
          });
          await this.safeDirectory(preserved);
          const backup = await fs.lstat(preserved, { bigint: true });
          if (!occupant && sameFile(backup, original)) await fs.rename(preserved, directory);
        }
        throw error;
      }
      published = true;
      return entry;
    } finally {
      if (!published) await stage.discard().catch(() => {});
    }
  }
}
