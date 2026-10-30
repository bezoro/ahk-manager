import fs from 'node:fs';
import path from 'node:path';
import type { DeploymentMode } from '../../shared/storage.js';
import { parseStorageJson, storageKeys, storageObject } from './json.js';

export const portableApplicationId = 'Bezoro.AhkManager.Electron';
export const portableConfigurationName = 'portable.json';
export const portableMoveName = 'portable-data-move.json';

export interface PortableConfiguration {
  schemaVersion: 1;
  applicationId: typeof portableApplicationId;
  dataDirectory: string;
}

export interface DeploymentLayout {
  mode: DeploymentMode;
  applicationDirectory: string;
  launchExecutable: string;
  portableRoot: string | null;
  configurationPath: string | null;
  dataDirectory: string;
  profileDirectory: string;
  chromiumDirectory: string | null;
  updatesDirectory: string;
  defaultDataDirectory: string;
  legacyDataDirectory: string;
}

const missing = (error: unknown): boolean => (error as NodeJS.ErrnoException)?.code === 'ENOENT';
export const samePath = (first: string, second: string): boolean =>
  process.platform === 'win32'
    ? path.resolve(first).toLowerCase() === path.resolve(second).toLowerCase()
    : path.resolve(first) === path.resolve(second);

export function containsPath(directory: string, candidate: string): boolean {
  const relative = path.relative(directory, candidate);
  return (
    !relative ||
    (relative !== '..' && !relative.startsWith('..' + path.sep) && !path.isAbsolute(relative))
  );
}

/** Existing path components may never redirect storage or its bootstrap configuration. */
export function assertStoragePath(value: string): void {
  for (let current = path.resolve(value); ; current = path.dirname(current)) {
    try {
      const info = fs.lstatSync(current);
      if (info.isSymbolicLink()) throw new Error('The data path contains a redirected directory.');
      if (current !== path.resolve(value) && !info.isDirectory())
        throw new Error('A data path parent is not a directory.');
    } catch (error) {
      if (!missing(error)) throw error;
    }
    if (path.dirname(current) === current) break;
  }
}

export function readStorageJson(file: string, maximum: number): unknown {
  assertStoragePath(file);
  const before = fs.lstatSync(file, { bigint: true });
  if (
    !before.isFile() ||
    before.isSymbolicLink() ||
    before.nlink !== 1n ||
    before.size < 2n ||
    before.size > BigInt(maximum)
  )
    throw new Error('The data settings file is not a regular bounded file.');
  const handle = fs.openSync(file, 'r');
  try {
    const opened = fs.fstatSync(handle, { bigint: true });
    if (opened.dev !== before.dev || opened.ino !== before.ino)
      throw new Error('The data settings file changed while it was opened.');
    const text = new TextDecoder('utf-8', { fatal: true }).decode(fs.readFileSync(handle));
    const after = fs.fstatSync(handle, { bigint: true });
    const current = fs.lstatSync(file, { bigint: true });
    if (
      after.size !== before.size ||
      after.mtimeNs !== before.mtimeNs ||
      after.ctimeNs !== before.ctimeNs ||
      current.dev !== before.dev ||
      current.ino !== before.ino
    )
      throw new Error('The data settings file changed while it was read.');
    return parseStorageJson(text);
  } finally {
    fs.closeSync(handle);
  }
}

function pathSegments(value: string): string[] {
  return value.split(path.sep);
}

export function validateDataDirectory(portableRoot: string, value: string): string {
  if (!value || value.length > 32760 || /[\x00-\x1f"<>|*?]/.test(value) || value.startsWith('\\\\'))
    throw new Error('Choose a local data folder.');
  if (process.platform === 'win32' && value.includes('/'))
    throw new Error('The data folder must use a normalized Windows path.');
  const absolute = path.isAbsolute(value);
  if (process.platform === 'win32' && absolute && !/^[A-Za-z]:\\/.test(value))
    throw new Error('Choose a local drive data folder.');
  if (!absolute && value.includes(':')) throw new Error('The relative data folder is invalid.');
  const segmentText = absolute ? value.slice(path.parse(value).root.length) : value;
  if (segmentText.includes(':') || (process.platform !== 'win32' && value.includes('\\')))
    throw new Error('The data folder contains a stream or separator alias.');
  if (
    pathSegments(segmentText).some(
      (segment) =>
        !segment ||
        segment === '.' ||
        segment === '..' ||
        /[. ]$/.test(segment) ||
        /^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)/i.test(segment),
    )
  )
    throw new Error('The data folder has an ambiguous path.');
  const result = path.resolve(portableRoot, value);
  if (absolute && result !== value) throw new Error('The data folder must be normalized.');
  const application = path.join(portableRoot, 'App');
  if (
    samePath(result, path.parse(result).root) ||
    containsPath(result, portableRoot) ||
    containsPath(application, result)
  )
    throw new Error('The data folder must be separate from the application files.');
  if (containsPath(portableRoot, result)) {
    const first = path.relative(portableRoot, result).split(path.sep)[0]!.toLowerCase();
    if (
      first.startsWith('.app-stage-') ||
      first.startsWith('.app-previous-') ||
      /^\.portable-data-(config|move)-.*\.tmp$/.test(first)
    )
      throw new Error('The data folder uses a reserved recovery name.');
  }
  for (const name of [
    'portable.json',
    'portable-package.json',
    'portable-data-move.json',
    'Bezoro.AhkManager.exe',
  ]) {
    if (containsPath(path.join(portableRoot, name), result))
      throw new Error('The data folder overlaps an application file.');
  }
  assertStoragePath(result);
  try {
    if (!fs.lstatSync(result).isDirectory())
      throw new Error('The data folder path refers to a file.');
  } catch (error) {
    if (!missing(error)) throw error;
  }
  return result;
}

export function parsePortableConfiguration(value: unknown): PortableConfiguration {
  if (
    !storageObject(value) ||
    !storageKeys(value, ['schemaVersion', 'applicationId', 'dataDirectory']) ||
    value.schemaVersion !== 1 ||
    value.applicationId !== portableApplicationId ||
    typeof value.dataDirectory !== 'string'
  )
    throw new Error('The portable data settings are invalid.');
  return value as unknown as PortableConfiguration;
}

/** Called before Electron creates its browser profile or takes its instance lock. */
export function resolveDeployment(
  executable: string,
  localAppDataDirectory: string,
): DeploymentLayout {
  if (!path.isAbsolute(executable) || !path.isAbsolute(localAppDataDirectory))
    throw new Error('Absolute application and local data paths are required.');
  const applicationDirectory = path.dirname(executable);
  const legacyDataDirectory = path.join(localAppDataDirectory, 'Bezoro', 'AhkManagerElectron');
  const portableRoot = path.dirname(applicationDirectory);
  const configurationPath = path.join(portableRoot, portableConfigurationName);
  let configuration: PortableConfiguration | null = null;
  if (path.basename(applicationDirectory).toLowerCase() === 'app') {
    try {
      configuration = parsePortableConfiguration(readStorageJson(configurationPath, 4096));
    } catch (error) {
      if (!missing(error)) throw error;
      try {
        fs.lstatSync(path.join(portableRoot, 'portable-package.json'));
        throw new Error(
          'The portable data settings file is missing. Restore portable.json before opening Manager.',
        );
      } catch (descriptorError) {
        if (!missing(descriptorError)) throw descriptorError;
      }
    }
  }
  if (!configuration)
    return {
      mode: 'installed',
      applicationDirectory,
      launchExecutable: executable,
      portableRoot: null,
      configurationPath: null,
      dataDirectory: legacyDataDirectory,
      profileDirectory: legacyDataDirectory,
      chromiumDirectory: null,
      updatesDirectory: path.join(legacyDataDirectory, 'Updates'),
      defaultDataDirectory: legacyDataDirectory,
      legacyDataDirectory,
    };
  assertStoragePath(portableRoot);
  const dataDirectory = validateDataDirectory(portableRoot, configuration.dataDirectory);
  return {
    mode: 'portable',
    applicationDirectory,
    launchExecutable: path.join(portableRoot, 'Bezoro.AhkManager.exe'),
    portableRoot,
    configurationPath,
    dataDirectory,
    profileDirectory: path.join(dataDirectory, 'Profile'),
    chromiumDirectory: path.join(dataDirectory, 'Chromium'),
    updatesDirectory: path.join(dataDirectory, 'Updates'),
    defaultDataDirectory: path.join(portableRoot, 'Data'),
    legacyDataDirectory,
  };
}
