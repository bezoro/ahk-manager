import fs from 'node:fs/promises';
import path from 'node:path';
import { createHash, randomUUID } from 'node:crypto';
import { createReadStream } from 'node:fs';
import {
  validUpdateChannel,
  validUpdateVersion,
  type UpdateChannel,
} from '../../shared/update-versions.js';

export interface CachedInstaller {
  version: string;
  installerPath: string;
  sha256: string;
  bytes: number;
}
const version = validUpdateVersion;
const digest = (value: unknown): value is string =>
  typeof value === 'string' && /^[a-f0-9]{64}$/i.test(value);
const missing = (error: unknown): boolean => (error as NodeJS.ErrnoException)?.code === 'ENOENT';

/** Cache is outside the installation and the operational profile. No script is copied. */
export class UpdateStore {
  constructor(readonly root: string) {
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
  async cached(): Promise<CachedInstaller[]> {
    const root = path.join(this.root, 'installers');
    try {
      await this.safeDirectory(root);
    } catch (error) {
      if (missing(error)) return [];
      throw error;
    }
    const result: CachedInstaller[] = [];
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
          record.updateProtocol !== 1 ||
          record.version !== name ||
          !digest(record.sha256)
        )
          continue;
        const installerPath = path.join(root, name, 'installer.exe');
        const stat = await this.regular(installerPath);
        if (record.bytes !== undefined && record.bytes !== stat.size) continue;
        result.push({
          version: name,
          installerPath,
          sha256: record.sha256.toLowerCase(),
          bytes: stat.size,
        });
      } catch {
        /* An incomplete or redirected cache never becomes an executable choice. */
      }
    }
    return result;
  }
  async verify(installer: CachedInstaller): Promise<void> {
    if (
      !version(installer.version) ||
      installer.installerPath !==
        path.join(this.root, 'installers', installer.version, 'installer.exe') ||
      !digest(installer.sha256)
    )
      throw new Error('The installer is outside the version history.');
    const stat = await this.regular(installer.installerPath);
    if (
      stat.size !== installer.bytes ||
      (await this.hash(installer.installerPath)) !== installer.sha256
    )
      throw new Error(
        'The installer integrity check failed. Check for updates to download another copy.',
      );
  }
  async cache(
    versionName: string,
    source: string,
    sha256: string,
    bytes: number,
  ): Promise<CachedInstaller> {
    if (!version(versionName) || !digest(sha256)) throw new Error('The update version is invalid.');
    if ((await this.regular(source)).size !== bytes || (await this.hash(source)) !== sha256)
      throw new Error('The downloaded installer failed its integrity check.');
    const directory = path.join(this.root, 'installers', versionName);
    await this.safeDirectory(directory, true);
    const installerPath = path.join(directory, 'installer.exe');
    const existing = (await this.cached()).find(
      (item) => item.version === versionName && item.sha256 === sha256 && item.bytes === bytes,
    );
    if (existing) {
      try {
        await this.verify(existing);
        return existing;
      } catch {
        /* Preserve corrupt bytes below and replace through a fresh file. */
      }
    }
    const temporary = path.join(directory, `download-${randomUUID()}.tmp`);
    await fs.copyFile(source, temporary, 1);
    if ((await this.hash(temporary)) !== sha256)
      throw new Error('The installer changed while preparing the update.');
    try {
      await this.regular(installerPath);
      await fs.rename(installerPath, path.join(directory, `preserved-${randomUUID()}.exe`));
    } catch (error) {
      if (!missing(error)) throw error;
    }
    await fs.rename(temporary, installerPath);
    const receipt = path.join(directory, 'receipt.json');
    const tempReceipt = path.join(directory, `receipt-${randomUUID()}.tmp`);
    await this.writeNew(
      tempReceipt,
      JSON.stringify({ schemaVersion: 1, updateProtocol: 1, version: versionName, sha256, bytes }),
    );
    await fs.rename(tempReceipt, receipt);
    return { version: versionName, installerPath, sha256, bytes };
  }
}
