import fs from 'node:fs/promises';
import { createReadStream } from 'node:fs';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { assertStoragePath } from './deployment.js';

export interface DirectoryIdentity {
  dev: string;
  ino: string;
  birthtimeNs: string;
}
export interface DataFile {
  relative: string;
  bytes: number;
  sha256: string;
}
export interface DataInventory {
  directories: string[];
  files: DataFile[];
}

const maximumFiles = 50000;
const maximumBytes = 16 * 1024 * 1024 * 1024;
const maximumFileBytes = 2 * 1024 * 1024 * 1024;
const maximumDirectories = 20000;
const missing = (error: unknown): boolean => (error as NodeJS.ErrnoException)?.code === 'ENOENT';
const fail = (message: string): Error => new Error(message);

export async function readDirectoryIdentity(directory: string): Promise<DirectoryIdentity> {
  assertStoragePath(directory);
  const info = await fs.lstat(directory, { bigint: true });
  if (!info.isDirectory() || info.isSymbolicLink()) throw fail('The data folder is invalid.');
  return { dev: String(info.dev), ino: String(info.ino), birthtimeNs: String(info.birthtimeNs) };
}

export function sameDirectoryIdentity(
  first: DirectoryIdentity,
  second: DirectoryIdentity,
): boolean {
  return (
    first.dev === second.dev && first.ino === second.ino && first.birthtimeNs === second.birthtimeNs
  );
}

export async function dataDirectoryExists(directory: string): Promise<boolean> {
  try {
    await fs.lstat(directory);
    return true;
  } catch (error) {
    if (missing(error)) return false;
    throw error;
  }
}

export async function assertEmptyDirectory(directory: string): Promise<void> {
  await readDirectoryIdentity(directory);
  if ((await fs.readdir(directory)).length)
    throw fail('Choose an empty data folder. Existing files will not be overwritten.');
}

async function fileHash(file: string): Promise<{ bytes: number; sha256: string }> {
  assertStoragePath(file);
  const before = await fs.lstat(file, { bigint: true });
  if (
    !before.isFile() ||
    before.isSymbolicLink() ||
    before.nlink !== 1n ||
    before.size > BigInt(maximumFileBytes)
  )
    throw fail('The data folder contains an unsupported linked, redirected or oversized file.');
  const hash = createHash('sha256');
  for await (const chunk of createReadStream(file)) hash.update(chunk);
  const after = await fs.lstat(file, { bigint: true });
  if (
    before.dev !== after.dev ||
    before.ino !== after.ino ||
    before.size !== after.size ||
    before.mtimeNs !== after.mtimeNs ||
    before.ctimeNs !== after.ctimeNs
  )
    throw fail(
      'A data file changed while it was being verified. Restart the move after other operations finish.',
    );
  return { bytes: Number(before.size), sha256: hash.digest('hex') };
}

function excluded(relative: string): boolean {
  const normalized = relative.split(path.sep).join('/').toLowerCase();
  return (
    normalized === 'chromium' ||
    normalized.startsWith('chromium/') ||
    normalized === 'updates/github-token.bin' ||
    path.basename(relative) === '.write.lock'
  );
}

export async function inventoryData(root: string, includeExcluded = false): Promise<DataInventory> {
  await readDirectoryIdentity(root);
  const result: DataInventory = { directories: [], files: [] };
  let totalBytes = 0;
  const walk = async (relative: string, depth: number): Promise<void> => {
    if (depth > 32) throw fail('The data folder has too many nested directories.');
    const directory = path.join(root, relative);
    await readDirectoryIdentity(directory);
    for (const entryName of await fs.readdir(directory)) {
      const name = relative ? path.join(relative, entryName) : entryName;
      if (!includeExcluded && excluded(name)) continue;
      // Windows directory enumeration labels cloud placeholders as links. lstat
      // distinguishes them from actual symlinks without following those links.
      const entry = await fs.lstat(path.join(root, name));
      if (entry.isSymbolicLink()) throw fail('The data folder contains a redirected entry.');
      if (entry.isDirectory()) {
        if (result.directories.length >= maximumDirectories)
          throw fail('The data folder contains too many directories.');
        result.directories.push(name);
        await walk(name, depth + 1);
      } else if (entry.isFile()) {
        if (result.files.length >= maximumFiles)
          throw fail('The data folder contains too many files.');
        const item = await fileHash(path.join(root, name));
        totalBytes += item.bytes;
        if (totalBytes > maximumBytes)
          throw fail('The data folder exceeds the supported move size.');
        result.files.push({ relative: name, ...item });
      } else throw fail('The data folder contains an unsupported entry.');
    }
  };
  await walk('', 0);
  result.directories.sort();
  result.files.sort((first, second) => first.relative.localeCompare(second.relative));
  return result;
}

export const fingerprintData = (value: DataInventory): string =>
  createHash('sha256').update(JSON.stringify(value)).digest('hex');

async function verifyPartialStage(stage: string, expected: DataInventory): Promise<void> {
  const current = await inventoryData(stage, true);
  const directories = new Set(expected.directories);
  const files = new Map(expected.files.map((file) => [file.relative, file]));
  if (
    current.directories.some((directory) => !directories.has(directory)) ||
    current.files.some((file) => {
      const original = files.get(file.relative);
      return !original || file.bytes !== original.bytes || file.sha256 !== original.sha256;
    })
  )
    throw fail(
      'The staged data changed or belongs to an earlier snapshot. The original folder was preserved; cancel this move and choose the location again.',
    );
}

export async function copyDataInventory(
  source: string,
  stage: string,
  expected: DataInventory,
  check: () => void = () => {},
): Promise<void> {
  check();
  await verifyPartialStage(stage, expected);
  for (const relative of expected.directories) {
    check();
    const directory = path.join(stage, relative);
    if (!(await dataDirectoryExists(directory))) await fs.mkdir(directory, { mode: 0o700 });
    check();
    await readDirectoryIdentity(directory);
  }
  for (const file of expected.files) {
    check();
    const destination = path.join(stage, file.relative);
    if (!(await dataDirectoryExists(destination)))
      await fs.copyFile(path.join(source, file.relative), destination, 1);
    check();
    const copied = await fileHash(destination);
    if (copied.bytes !== file.bytes || copied.sha256 !== file.sha256)
      throw fail(
        'The data copy did not match the original file. The original folder was preserved.',
      );
  }
  check();
  if (fingerprintData(await inventoryData(stage, true)) !== fingerprintData(expected))
    throw fail('The complete data copy could not be verified.');
}
