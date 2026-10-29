import fs from 'node:fs/promises';
import path from 'node:path';
import { createHash, randomUUID } from 'node:crypto';
import { createReadStream } from 'node:fs';

export interface CachedInstaller {
  version: string;
  installerPath: string;
  sha256: string;
  bytes: number;
}
export interface TokenEncryption {
  available(): boolean;
  encrypt(value: string): Buffer;
  decrypt(value: Buffer): string;
}
const version = (value: unknown): value is string =>
  typeof value === 'string' && /^(0|[1-9]\d{0,8})\.(0|[1-9]\d{0,8})\.(0|[1-9]\d{0,8})$/.test(value);
const digest = (value: unknown): value is string =>
  typeof value === 'string' && /^[a-f0-9]{64}$/i.test(value);
const missing = (error: unknown): boolean => (error as NodeJS.ErrnoException)?.code === 'ENOENT';

/** Cache is outside the installation and the operational profile. No script is copied. */
export class UpdateStore {
  constructor(
    readonly root: string,
    private readonly encryption: TokenEncryption,
  ) {
    if (!path.isAbsolute(root)) throw new Error('Cache de atualização inválido.');
  }
  async safeDirectory(directory: string, create = false): Promise<void> {
    const absolute = path.resolve(directory);
    const parent = path.dirname(absolute);
    if (parent !== absolute) await this.safeDirectory(parent, create);
    try {
      const stat = await fs.lstat(absolute);
      if (!stat.isDirectory() || stat.isSymbolicLink())
        throw new Error('A pasta de atualização foi redirecionada.');
    } catch (error) {
      if (!create || !missing(error)) throw error;
      try {
        await fs.mkdir(absolute, { mode: 0o700 });
      } catch (nested) {
        if ((nested as NodeJS.ErrnoException).code !== 'EEXIST') throw nested;
      }
      const stat = await fs.lstat(absolute);
      if (!stat.isDirectory() || stat.isSymbolicLink())
        throw new Error('Pasta de atualização inválida.');
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
      throw new Error('Arquivo de atualização inválido.');
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
      throw new Error('O arquivo de atualização mudou durante a verificação.');
    return hash.digest('hex');
  }
  async writeNew(file: string, value: string | Buffer): Promise<void> {
    await this.safeDirectory(path.dirname(file), true);
    await fs.writeFile(file, value, { flag: 'wx', mode: 0o600 });
  }
  async readToken(): Promise<string | null> {
    const file = path.join(this.root, 'github-token.bin');
    try {
      await this.regular(file, 16384);
      if (!this.encryption.available())
        throw new Error('A proteção de credenciais do Windows está indisponível.');
      const value = this.encryption.decrypt(await fs.readFile(file));
      if (!/^[A-Za-z0-9_]{20,512}$/.test(value))
        throw new Error('A credencial de atualização precisa ser configurada novamente.');
      return value;
    } catch (error) {
      if (missing(error)) return null;
      throw new Error(
        'Não foi possível ler o acesso protegido ao GitHub. Configure o acesso novamente.',
      );
    }
  }
  async saveToken(value: string | null): Promise<void> {
    const file = path.join(this.root, 'github-token.bin');
    await this.safeDirectory(this.root, true);
    if (value === null) {
      try {
        await this.regular(file, 16384);
        await fs.unlink(file);
      } catch (error) {
        if (!missing(error)) throw error;
      }
      return;
    }
    if (!/^[A-Za-z0-9_]{20,512}$/.test(value))
      throw new Error('Informe um token do GitHub válido.');
    if (!this.encryption.available())
      throw new Error(
        'O Windows não disponibilizou a proteção de credenciais. O token não foi salvo.',
      );
    const temporary = path.join(this.root, `credential-${randomUUID()}.tmp`);
    await this.writeNew(temporary, this.encryption.encrypt(value));
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
      throw new Error('Instalador fora do histórico de versões.');
    const stat = await this.regular(installer.installerPath);
    if (
      stat.size !== installer.bytes ||
      (await this.hash(installer.installerPath)) !== installer.sha256
    )
      throw new Error(
        'A integridade do instalador não confere. Verifique as atualizações para baixar outra cópia.',
      );
  }
  async cache(
    versionName: string,
    source: string,
    sha256: string,
    bytes: number,
  ): Promise<CachedInstaller> {
    if (!version(versionName) || !digest(sha256))
      throw new Error('Versão de atualização inválida.');
    if ((await this.regular(source)).size !== bytes || (await this.hash(source)) !== sha256)
      throw new Error('O instalador baixado não passou na verificação de integridade.');
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
      throw new Error('O instalador mudou ao preparar a atualização.');
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
