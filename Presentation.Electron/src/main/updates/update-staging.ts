import fs, { type FileHandle } from 'node:fs/promises';
import type { BigIntStats } from 'node:fs';
import path from 'node:path';

function sameEntry(a: BigIntStats, b: BigIntStats): boolean {
  return a.dev === b.dev && a.ino === b.ino && a.birthtimeNs === b.birthtimeNs;
}

/** A private flat workspace. Only files opened exclusively by this instance are removable. */
export class UpdateStaging {
  private readonly files = new Map<string, BigIntStats>();

  private constructor(
    readonly directory: string,
    private readonly identity: BigIntStats,
    private readonly safeDirectory: (directory: string) => Promise<void>,
  ) {}

  static async create(
    prefix: string,
    check: (directory: string) => Promise<void>,
  ): Promise<UpdateStaging> {
    await check(path.dirname(prefix));
    const directory = await fs.mkdtemp(prefix);
    return new UpdateStaging(directory, await fs.lstat(directory, { bigint: true }), check);
  }

  private async checkDirectory(): Promise<void> {
    await this.safeDirectory(this.directory);
    const entry = await fs.lstat(this.directory, { bigint: true });
    if (!entry.isDirectory() || !sameEntry(entry, this.identity))
      throw new Error('Update staging changed.');
  }

  async open(name: string): Promise<FileHandle> {
    if (path.basename(name) !== name) throw new Error('Invalid update staging name.');
    await this.checkDirectory();
    const file = await fs.open(path.join(this.directory, name), 'wx', 0o600);
    try {
      this.files.set(name, await file.stat({ bigint: true }));
      return file;
    } catch (error) {
      await file.close();
      throw error;
    }
  }

  async write(name: string, bytes: Buffer | string): Promise<void> {
    const file = await this.open(name);
    try {
      await file.writeFile(bytes);
      await file.sync();
    } finally {
      await file.close();
    }
  }

  private async checkFile(name: string): Promise<void> {
    const owned = this.files.get(name);
    const entry = await fs.lstat(path.join(this.directory, name), { bigint: true });
    if (!owned || !entry.isFile() || entry.nlink !== 1n || !sameEntry(entry, owned))
      throw new Error('Update staging contents changed.');
  }

  async verify(): Promise<void> {
    await this.checkDirectory();
    const members = await fs.readdir(this.directory);
    if (members.length !== this.files.size) throw new Error('Update staging contents changed.');
    for (const name of members) await this.checkFile(name);
  }

  async discard(): Promise<void> {
    await this.verify();
    for (const name of this.files.keys()) {
      await this.checkDirectory();
      await this.checkFile(name);
      await fs.unlink(path.join(this.directory, name));
      this.files.delete(name);
    }
    await this.checkDirectory();
    await fs.rmdir(this.directory);
  }
}
